using Stride.Core.Assets;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using Stride.Core.Diagnostics;
using CoreLogger = Stride.Core.Diagnostics.ILogger;
using MicrosoftProjectCollection = Microsoft.Build.Evaluation.ProjectCollection;

namespace Stride.Editor.ImGui;

internal sealed record AuthoredProjectSnapshot(string Path, string Xml, long Revision, byte[] SavedFileHash);
internal sealed record AuthoredResolvedProjectReference(string ProjectPath, string Framework, string[] Aliases, bool EmbedInteropTypes, string AssemblyPath);
internal sealed record AuthoredResolvedCompilation(string ProjectPath, string Framework, string AssemblyName, string[] CommandLineArguments,
    AuthoredResolvedProjectReference[] ProjectReferences, string AssetsFilePath, string? AssemblyPath = null,
    bool? RunAnalyzers = null, bool? RunAnalyzersDuringLiveAnalysis = null);
internal sealed record AuthoredProjectInput(string Path, byte[] Hash);

/// <summary>Owns actual SDK/NuGet compiler artifacts produced from immutable authored XML.</summary>
internal sealed class AuthoredProjectCompilationResolution : IDisposable
{
    internal AuthoredProjectCompilationResolution(string directory, AuthoredResolvedCompilation[] projects, AuthoredProjectInput[] inputs)
    { Directory = directory; Projects = projects; Inputs = inputs; }
    public string Directory { get; }
    public IReadOnlyList<AuthoredResolvedCompilation> Projects { get; }
    public IReadOnlyList<AuthoredProjectInput> Inputs { get; }
    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        AuthoredProjectCompilationResolver.RemoveArtifacts(Directory);
        disposed = true;
    }
}

internal static partial class AuthoredProjectCompilationResolver
{
    private static readonly string artifactRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride.ImGui", "semantic-projects"));
    public static async Task<AuthoredProjectCompilationResolution> Resolve(AuthoredProjectSnapshot[] snapshots, string entryProject,
        string configuration, string framework, CoreLogger logger, CancellationToken cancellation, bool compile = false)
    {
        foreach (var snapshot in snapshots) VerifySnapshot(snapshot);
        var directory = Path.Combine(artifactRoot, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var collection = new MicrosoftProjectCollection();
            var roots = new ConcurrentDictionary<string, ProjectRootElement>(StringComparer.OrdinalIgnoreCase);
            var inputs = new Dictionary<string, AuthoredProjectInput>(StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in snapshots)
            {
                using var reader = XmlReader.Create(new StringReader(snapshot.Xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                var root = ProjectRootElement.Create(reader, collection, preserveFormatting: true); root.FullPath = Path.GetFullPath(snapshot.Path);
                if (!roots.TryAdd(root.FullPath, root)) throw new InvalidOperationException("The authored project snapshot is duplicated.");
            }
            var globals = new Dictionary<string, string>
            {
                ["Configuration"] = configuration, ["DesignTimeBuild"] = compile ? "false" : "true", ["BuildingInsideVisualStudio"] = "true",
                ["BuildProjectReferences"] = "false", ["SkipCompilerExecution"] = compile ? "false" : "true", ["ProvideCommandLineArgs"] = "true",
                ["StrideSkipAutoPack"] = "true", ["RestoreRecursive"] = "false",
            };
            if (framework.Length > 0) globals["TargetFramework"] = framework;
            ProjectGraph EvaluateGraph(bool usePrivateArtifacts) => new([new ProjectGraphEntryPoint(Path.GetFullPath(entryProject), globals)], collection,
                (path, properties, projectCollection) =>
                {
                    var owned = new Dictionary<string, string>(properties);
                    if (usePrivateArtifacts) SetArtifactProperties(owned, directory, path, owned.TryGetValue("TargetFramework", out var target) ? target : "");
                    var root = roots.GetOrAdd(Path.GetFullPath(path), file => ReadOriginalRoot(file, projectCollection, inputs));
                    return new ProjectInstance(root, owned, null, projectCollection);
                }, cancellation);
            var graph = EvaluateGraph(false);
            var seenGraphs = new HashSet<string>(StringComparer.Ordinal);
            var converged = false;
            for (var iteration = 0; iteration < 12; iteration++)
            {
            cancellation.ThrowIfCancellationRequested();
            var before = GraphFingerprint(graph, directory);
            if (!seenGraphs.Add(before)) throw new InvalidOperationException("The SDK restore graph did not converge.");
            foreach (var node in graph.ProjectNodes)
            {
                foreach (var path in node.ProjectInstance.ImportPaths.Concat(node.ProjectInstance.GetPropertyValue("MSBuildAllProjects").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Append(node.ProjectInstance.FullPath).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(path) && !Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) CaptureInput(inputs, path);
            }
            var packageGraph = GenerateNativeRestoreGraph(graph, roots, collection, directory, inputs, logger, cancellation);
            foreach (var node in graph.ProjectNodes)
            {
                var spec = packageGraph.Projects.SingleOrDefault(spec => SamePath(spec.RestoreMetadata.ProjectUniqueName, node.ProjectInstance.FullPath))
                    ?? throw new InvalidOperationException("MSBuild omitted an authored project from its native restore graph.");
                var target = node.ProjectInstance.GetPropertyValue("TargetFramework");
                var infos = spec.TargetFrameworks.Where(info => target.Length == 0 || info.TargetAlias == target || info.FrameworkName.GetShortFolderName() == target).ToArray();
                if (infos.Length == 0) throw new InvalidOperationException("MSBuild omitted an authored target framework from its native restore graph.");
                foreach (var reference in node.ProjectInstance.GetItems("PackageReference"))
                {
                    var declared = reference.GetMetadataValue("VersionOverride");
                    if (declared.Length == 0) declared = reference.GetMetadataValue("Version");
                    foreach (var info in infos)
                    {
                        var dependency = info.Dependencies.SingleOrDefault(dependency => string.Equals(dependency.Name, reference.EvaluatedInclude, StringComparison.OrdinalIgnoreCase));
                        if (dependency == null || declared.Length > 0 && !NuGet.Versioning.VersionRange.Parse(declared).Equals(dependency.LibraryRange.VersionRange))
                            throw new InvalidOperationException("The SDK restore graph differs from the authored package-reference snapshot.");
                    }
                }
            }
            // This is a real NuGet resolution of the SDK-generated graph. The
            // project's .csproj/.user/obj artifacts are never its output target.
            using (var cache = new SourceCacheContext())
            {
                var settings = NuGet.Configuration.Settings.LoadDefaultSettings(Path.GetDirectoryName(entryProject));
                var nativeLogger = new ResolutionNuGetLogger(logger);
                var args = new RestoreArgs { AllowNoOp = true, CacheContext = cache, Log = nativeLogger,
                    CachingSourceProvider = new CachingSourceProvider(new PackageSourceProvider(settings)) };
                var provider = new DependencyGraphSpecRequestProvider(new RestoreCommandProvidersCache(), packageGraph);
                var requests = await provider.CreateRequests(args).ConfigureAwait(false);
                foreach (var request in requests)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var result = await new RestoreCommand(request.Request).ExecuteAsync(cancellation).ConfigureAwait(false);
                    if (!result.Success) throw new InvalidOperationException("NuGet could not resolve the authored project references.");
                    await result.CommitAsync(nativeLogger, cancellation).ConfigureAwait(false);
                }
            }
            foreach (var snapshot in snapshots) VerifySnapshot(snapshot);
            foreach (var input in inputs.Values)
                if (!ReadInput(input.Path).Hash.AsSpan().SequenceEqual(input.Hash)) throw new InvalidOperationException("A project input changed during native reference resolution.");
            foreach (var import in graph.ProjectNodes.SelectMany(node => node.ProjectInstance.ImportPaths).Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(import) && Path.GetFullPath(import).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    ProjectRootElement.TryOpen(import, collection)?.Reload(throwIfUnsavedChanges: false);
            var nextGraph = EvaluateGraph(true);
            var after = GraphFingerprint(nextGraph, directory);
            graph = nextGraph;
            if (before == after) { converged = true; break; }
            }
            if (!converged) throw new InvalidOperationException("The SDK restore graph exceeded its convergence limit.");
            var resolved = new List<AuthoredResolvedCompilation>();
            var compiledOutputs = new Dictionary<(string Path, string Framework), string>();
            using (var manager = new BuildManager())
            {
                manager.BeginBuild(new BuildParameters(collection) { Loggers = [new ResolutionBuildLogger(logger)], DisableInProcNode = true, EnableNodeReuse = false });
                using var registration = cancellation.Register(manager.CancelAllSubmissions);
                try
                {
                    foreach (var node in graph.ProjectNodesTopologicallySorted)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var original = node.ProjectInstance;
                        if (string.Equals(original.GetPropertyValue("IsCrossTargetingBuild"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                        var projectDirectory = ProjectArtifactDirectory(directory, original.FullPath);
                        var properties = original.GlobalProperties.ToDictionary(property => property.Key, property => property.Value);
                        SetArtifactProperties(properties, directory, original.FullPath, original.GetPropertyValue("TargetFramework"));
                        if (compile)
                        {
                            var fileName = original.GetPropertyValue("TargetFileName");
                            if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName) throw new InvalidOperationException("The native project's target file name is invalid.");
                            properties["TargetPath"] = Path.Combine(properties["OutputPath"], fileName);
                            if (original.GetPropertyValue("DocumentationFile").Length > 0) properties["DocumentationFile"] = Path.Combine(properties["OutputPath"], Path.GetFileNameWithoutExtension(fileName) + ".xml");
                            if (original.GetPropertyValue("ErrorLog").Length > 0) properties["ErrorLog"] = Path.Combine(projectDirectory, "compiler.sarif");
                        }
                        var root = roots.GetValueOrDefault(original.FullPath) ?? ProjectRootElement.Open(original.FullPath, collection);
                        if (compile)
                        {
                            root = CreateRuntimeRoot(root, node, compiledOutputs, collection);
                            foreach (var item in original.GetItems("Compile"))
                            {
                                var path = Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(original.FullPath)!);
                                if (File.Exists(path) && !path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) CaptureInput(inputs, path);
                            }
                        }
                        var instance = new ProjectInstance(root, properties, original.ToolsVersion, collection);
                        foreach (var reference in node.ProjectReferences)
                            foreach (var item in instance.GetItems("ProjectReference").Where(item => SamePath(item.GetMetadataValue("FullPath"), reference.ProjectInstance.FullPath)))
                            {
                                // The real static graph already negotiated the
                                // nearest framework/platform from retained XML.
                                item.SetMetadata("SkipGetTargetFrameworkProperties", "true");
                                item.SetMetadata("SkipGetPlatformProperties", "true");
                                item.SetMetadata("SetTargetFramework", "TargetFramework=" + reference.ProjectInstance.GetPropertyValue("TargetFramework"));
                                if (compile) item.SetMetadata("BuildReference", "false");
                            }
                        var result = manager.BuildRequest(new BuildRequestData(instance, [compile ? "Build" : "Compile"], null, BuildRequestDataFlags.ProvideProjectStateAfterBuild));
                        if (result.OverallResult != BuildResultCode.Success) throw new InvalidOperationException("MSBuild could not produce the authored compiler context.");
                        var state = result.ProjectStateAfterBuild ?? throw new InvalidOperationException("MSBuild returned no compiler project state.");
                        foreach (var path in state.ImportPaths.Concat(state.GetPropertyValue("MSBuildAllProjects").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                            if (File.Exists(path) && !Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                CaptureInput(inputs, path);
                        var commandLine = state.GetItems("CscCommandLineArgs").Select(item => item.EvaluatedInclude).ToArray();
                        if (commandLine.Length == 0) throw new InvalidOperationException("MSBuild returned no C# compiler arguments.");
                        var compilerReferences = state.GetItems("ProjectReference").Where(item => !string.Equals(item.GetMetadataValue("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (!compilerReferences.Select(item => Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(original.FullPath)!)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                            .SetEquals(node.ProjectReferences.Where(reference => compilerReferences.Any(item => SamePath(item.GetMetadataValue("FullPath"), reference.ProjectInstance.FullPath)))
                                .Select(reference => Path.GetFullPath(reference.ProjectInstance.FullPath))))
                            throw new InvalidOperationException("The native SDK dependency graph changed during compiler-context evaluation.");
                        var references = node.ProjectReferences.Where(reference =>
                        {
                            return compilerReferences.Any(item => SamePath(item.GetMetadataValue("FullPath"), reference.ProjectInstance.FullPath));
                        }).Select(reference =>
                        {
                            var item = compilerReferences.First(item => SamePath(item.GetMetadataValue("FullPath"), reference.ProjectInstance.FullPath));
                            return new AuthoredResolvedProjectReference(reference.ProjectInstance.FullPath, reference.ProjectInstance.GetPropertyValue("TargetFramework"),
                                item.GetMetadataValue("Aliases").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                                bool.TryParse(item.GetMetadataValue("EmbedInteropTypes"), out var embed) && embed,
                                state.GetItems("_ResolvedProjectReferencePaths").FirstOrDefault(resolved => SamePath(resolved.GetMetadataValue("MSBuildSourceProjectFile"), reference.ProjectInstance.FullPath))?.EvaluatedInclude
                                    ?? reference.ProjectInstance.GetPropertyValue("TargetPath"));
                        }).ToArray();
                        string? assemblyPath = null;
                        if (compile)
                        {
                            assemblyPath = Path.GetFullPath(state.GetPropertyValue("TargetPath"), Path.GetDirectoryName(original.FullPath)!);
                            if (!assemblyPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
                                throw new InvalidOperationException("MSBuild did not produce its assembly in the owned compilation directory.");
                            compiledOutputs.Add((Path.GetFullPath(original.FullPath).ToUpperInvariant(), original.GetPropertyValue("TargetFramework")), assemblyPath);
                        }
                        resolved.Add(new(original.FullPath, original.GetPropertyValue("TargetFramework"), state.GetPropertyValue("AssemblyName"), commandLine, references,
                            Path.Combine(projectDirectory, LockFileFormat.AssetsFileName), assemblyPath,
                            bool.TryParse(state.GetPropertyValue("RunAnalyzers"), out var runAnalyzers) ? runAnalyzers : null,
                            bool.TryParse(state.GetPropertyValue("RunAnalyzersDuringLiveAnalysis"), out var runLiveAnalyzers) ? runLiveAnalyzers : null));
                    }
                }
                finally { manager.EndBuild(); }
            }
            foreach (var snapshot in snapshots) VerifySnapshot(snapshot);
            foreach (var input in inputs.Values)
                if (!ReadInput(input.Path).Hash.AsSpan().SequenceEqual(input.Hash)) throw new InvalidOperationException("A project input changed during native reference resolution.");
            return new(directory, resolved.ToArray(), inputs.Values.ToArray());
        }
        catch { RemoveArtifacts(directory); throw; }
    }
    private static bool SamePath(string first, string second) => !string.IsNullOrEmpty(first)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static void SetArtifactProperties(Dictionary<string, string> properties, string directory, string path, string framework)
    {
        var projectDirectory = ProjectArtifactDirectory(directory, path);
        properties["MSBuildProjectExtensionsPath"] = projectDirectory + Path.DirectorySeparatorChar;
        properties["ProjectAssetsFile"] = Path.Combine(projectDirectory, LockFileFormat.AssetsFileName);
        properties["IntermediateOutputPath"] = Path.Combine(projectDirectory, framework) + Path.DirectorySeparatorChar;
        properties["OutputPath"] = Path.Combine(projectDirectory, "bin", framework) + Path.DirectorySeparatorChar;
        properties["OutDir"] = properties["OutputPath"];
        properties["TargetDir"] = properties["OutputPath"];
    }
    private static string GraphFingerprint(ProjectGraph graph, string directory)
    {
        var rows = new List<string>();
        foreach (var node in graph.ProjectNodes)
        {
            var instance = node.ProjectInstance;
            var key = Path.GetFullPath(instance.FullPath).ToUpperInvariant() + "::" + instance.GetPropertyValue("TargetFramework");
            rows.Add("project:" + key);
            foreach (var property in new[] { "TargetFrameworks", "RuntimeIdentifier", "RuntimeIdentifiers", "RestoreSources", "RestoreAdditionalProjectSources", "RestorePackagesPath", "ManagePackageVersionsCentrally", "CentralPackageTransitivePinningEnabled" })
                rows.Add(key + ":property:" + property + "=" + instance.GetPropertyValue(property));
            foreach (var itemType in new[] { "PackageReference", "PackageVersion", "ProjectReference", "PackageDownload", "FrameworkReference" })
                foreach (var item in instance.GetItems(itemType))
                    rows.Add(key + ":item:" + itemType + ":" + item.EvaluatedInclude + ":" + string.Join(";", item.Metadata.OrderBy(metadata => metadata.Name, StringComparer.Ordinal).Select(metadata => metadata.Name + "=" + metadata.EvaluatedValue)));
            foreach (var reference in node.ProjectReferences)
                rows.Add(key + ":edge:" + Path.GetFullPath(reference.ProjectInstance.FullPath).ToUpperInvariant() + "::" + reference.ProjectInstance.GetPropertyValue("TargetFramework"));
            foreach (var import in instance.ImportPaths.Where(path => Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                if (File.Exists(import)) rows.Add(key + ":generated-import:" + Path.GetFileName(import) + ":" + Convert.ToHexString(ReadInput(import).Hash));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows.Order(StringComparer.Ordinal)))));
    }
    private static string ProjectArtifactDirectory(string root, string project)
    {
        var path = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project).ToUpperInvariant()))));
        System.IO.Directory.CreateDirectory(path); return path;
    }
    private static AuthoredProjectInput ReadInput(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(Path.GetFullPath(path), SHA256.HashData(stream));
    }
    private static void CaptureInput(Dictionary<string, AuthoredProjectInput> inputs, string path)
    {
        var input = ReadInput(path);
        if (inputs.TryGetValue(input.Path, out var previous) && !previous.Hash.AsSpan().SequenceEqual(input.Hash))
            throw new InvalidOperationException("A project input changed during native reference resolution.");
        inputs[input.Path] = input;
    }
    private static void VerifySnapshot(AuthoredProjectSnapshot snapshot)
    {
        if (!ReadInput(snapshot.Path).Hash.AsSpan().SequenceEqual(snapshot.SavedFileHash))
            throw new InvalidOperationException("The project file changed outside its authored snapshot.");
    }
    internal static void RemoveArtifacts(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The compiler artifact directory is outside its owned root.");
        if (System.IO.Directory.Exists(full)) System.IO.Directory.Delete(full, true);
    }
    private sealed class ResolutionBuildLogger(CoreLogger logger) : Microsoft.Build.Utilities.Logger
    {
        public override void Initialize(Microsoft.Build.Framework.IEventSource source)
        {
            source.ErrorRaised += (_, args) => logger.Error(args.File + "(" + args.LineNumber + "): " + args.Code + ": " + args.Message);
            source.WarningRaised += (_, args) => logger.Warning(args.File + "(" + args.LineNumber + "): " + args.Code + ": " + args.Message);
        }
    }
    private sealed class ResolutionNuGetLogger(CoreLogger logger) : NuGet.Common.ILogger
    {
        public void Log(NuGet.Common.LogLevel level, string text)
        {
            if (level == NuGet.Common.LogLevel.Error) logger.Error(text);
            else if (level == NuGet.Common.LogLevel.Warning) logger.Warning(text);
            else logger.Info(text);
        }
        public void Log(NuGet.Common.ILogMessage message) => Log(message.Level, message.Message);
        public Task LogAsync(NuGet.Common.LogLevel level, string data) { Log(level, data); return Task.CompletedTask; }
        public Task LogAsync(NuGet.Common.ILogMessage message) { Log(message); return Task.CompletedTask; }
        public void LogDebug(string data) => Log(NuGet.Common.LogLevel.Debug, data);
        public void LogVerbose(string data) => Log(NuGet.Common.LogLevel.Verbose, data);
        public void LogInformation(string data) => Log(NuGet.Common.LogLevel.Information, data);
        public void LogMinimal(string data) => Log(NuGet.Common.LogLevel.Minimal, data);
        public void LogWarning(string data) => Log(NuGet.Common.LogLevel.Warning, data);
        public void LogError(string data) => Log(NuGet.Common.LogLevel.Error, data);
        public void LogInformationSummary(string data) => LogInformation(data);
    }
}
