using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class ScriptSemanticWorkspace
{
    private AuthoredProjectSnapshot[] authoredProjects = [];
    private AdhocWorkspace? authoredWorkspace;
    private AuthoredProjectCompilationResolution? authoredResolution;
    private NativeAnalyzerLoader? authoredAnalyzerLoader;
    private static readonly ConcurrentDictionary<string, AuthoredProjectCompilationResolution> retiredResolutions = new(StringComparer.OrdinalIgnoreCase);

    public void SetProjectSnapshots(AuthoredProjectSnapshot[] snapshots)
    {
        var next = snapshots.Select(snapshot => snapshot with { SavedFileHash = snapshot.SavedFileHash.ToArray() }).ToArray();
        var previous = Volatile.Read(ref authoredProjects);
        if (previous.Length == next.Length && previous.Zip(next).All(pair =>
            pair.First.Path == pair.Second.Path && pair.First.Revision == pair.Second.Revision && pair.First.Xml == pair.Second.Xml
            && pair.First.SavedFileHash.AsSpan().SequenceEqual(pair.Second.SavedFileHash))) return;
        Volatile.Write(ref authoredProjects, next);
        foreach (var snapshot in next) ObserveProjectOrigin(snapshot.Path);
        Interlocked.Increment(ref epoch);
    }

    private async Task LoadAuthored(long expectedEpoch, long loadingNativeVersion, CancellationToken cancellation)
    {
        var snapshots = Volatile.Read(ref authoredProjects);
        foreach (var snapshot in snapshots) ObserveEvaluatedImportPatterns(snapshot.Path,
            string.Equals(snapshot.Path, projectPath, StringComparison.OrdinalIgnoreCase) ? framework : "");
        var resolution = await AuthoredProjectCompilationResolver.Resolve(snapshots, projectPath, configuration, framework,
            new LoggerResult(), cancellation).ConfigureAwait(false);
        codeActionProviders ??= new ScriptCodeActionProviders();
        var next = new AdhocWorkspace(codeActionProviders.Host);
        var loader = new NativeAnalyzerLoader();
        try
        {
            var projects = resolution.Projects.Where(project => project.CommandLineArguments.Length > 0).ToArray();
            var ids = projects.ToDictionary(project => (Path.GetFullPath(project.ProjectPath).ToUpperInvariant(), project.Framework),
                project => ProjectId.CreateNewId(project.ProjectPath + "::" + project.Framework));
            var solution = next.CurrentSolution;
            foreach (var project in projects)
            {
                cancellation.ThrowIfCancellationRequested();
                var folder = Path.GetDirectoryName(project.ProjectPath)!;
                var arguments = CSharpCommandLineParser.Default.Parse(project.CommandLineArguments, folder, null);
                var failures = arguments.Errors.Where(error => error.Severity == DiagnosticSeverity.Error).ToArray();
                if (failures.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Select(error => error.ToString())));
                var id = ids[(Path.GetFullPath(project.ProjectPath).ToUpperInvariant(), project.Framework)];
                var replaced = project.ProjectReferences.Where(reference => !string.IsNullOrEmpty(reference.AssemblyPath)).Select(reference => Path.GetFullPath(reference.AssemblyPath, folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var references = arguments.MetadataReferences.Where(reference => !replaced.Contains(Path.GetFullPath(reference.Reference, folder))).Select(reference =>
                {
                    var path = ResolveCompilerPath(reference.Reference, folder, arguments.ReferencePaths);
                    return (MetadataReference)MetadataReference.CreateFromFile(path, reference.Properties);
                }).Where(reference => reference is not PortableExecutableReference file || file.FilePath == null || !replaced.Contains(Path.GetFullPath(file.FilePath))).ToArray();
                solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), Path.GetFileNameWithoutExtension(project.ProjectPath),
                    project.AssemblyName, LanguageNames.CSharp, filePath: project.ProjectPath,
                    outputFilePath: arguments.OutputFileName == null ? null : Path.Combine(arguments.OutputDirectory ?? folder, arguments.OutputFileName),
                    compilationOptions: arguments.CompilationOptions, parseOptions: arguments.ParseOptions,
                    metadataReferences: references, analyzerReferences: arguments.ResolveAnalyzerReferences(loader)));
                foreach (var source in arguments.SourceFiles)
                    solution = solution.AddDocument(CreateCompilerDocument(id, source.Path, folder, arguments.Encoding));
                foreach (var source in arguments.AdditionalFiles)
                {
                    var path = Path.GetFullPath(source.Path, folder);
                    solution = solution.AddAdditionalDocument(DocumentId.CreateNewId(id, path), Path.GetFileName(path),
                        SourceText.From(await File.ReadAllTextAsync(path, cancellation).ConfigureAwait(false)), filePath: path);
                }
                foreach (var path in arguments.AnalyzerConfigPaths)
                    solution = solution.AddAnalyzerConfigDocuments([CreateCompilerDocument(id, path, folder)]);
            }
            foreach (var project in projects)
            {
                var id = ids[(Path.GetFullPath(project.ProjectPath).ToUpperInvariant(), project.Framework)];
                foreach (var reference in project.ProjectReferences)
                {
                    if (!ids.TryGetValue((Path.GetFullPath(reference.ProjectPath).ToUpperInvariant(), reference.Framework), out var target))
                        throw new InvalidOperationException("The SDK compiler context omitted a referenced project target framework.");
                    solution = solution.AddProjectReference(id, new Microsoft.CodeAnalysis.ProjectReference(target, reference.Aliases.ToImmutableArray(), reference.EmbedInteropTypes));
                }
            }
            if (!next.TryApplyChanges(solution)) throw new InvalidOperationException("The authored compiler workspace could not load the SDK solution.");
            cancellation.ThrowIfCancellationRequested();
            if (Epoch != expectedEpoch) throw new OperationCanceledException("The authored project context changed.", cancellation);
            InstallWatchers(next.CurrentSolution);
            foreach (var input in resolution.Inputs) ObserveFile(input.Path);
            ReleaseAuthoredWorkspace();
            authoredWorkspace = next; authoredResolution = resolution; authoredAnalyzerLoader = loader;
            nativeContextGeneration = new(() =>
            {
                DisposeNativeContext(next.Dispose); DisposeNativeContext(loader.Dispose); RetireResolution(resolution);
            });
            RefreshReferenceContextLeases();
            baseline = next.CurrentSolution; loadedNativeVersion = loadingNativeVersion;
            analyzerPolicies = projects.ToDictionary(project => ids[(Path.GetFullPath(project.ProjectPath).ToUpperInvariant(), project.Framework)],
                project => new ScriptAnalyzerPolicy(project.RunAnalyzers, project.RunAnalyzersDuringLiveAnalysis));
            loadedProjectFiles = resolution.Inputs.Select(input => new ScriptProjectFileBaseline(input.Path, input.Hash.ToArray())).ToArray();
        }
        catch { next.Dispose(); loader.Dispose(); RetireResolution(resolution); throw; }
    }

    private static DocumentInfo CreateCompilerDocument(ProjectId project, string path, string folder, System.Text.Encoding? encoding = null)
    {
        var fullPath = Path.GetFullPath(path, folder);
        return DocumentInfo.Create(DocumentId.CreateNewId(project, fullPath), Path.GetFileName(fullPath),
            loader: new FileTextLoader(fullPath, encoding), filePath: fullPath);
    }
    private static string ResolveCompilerPath(string path, string folder, IEnumerable<string> searchPaths)
    {
        foreach (var candidate in new[] { Path.GetFullPath(path, folder) }.Concat(searchPaths.Select(search => Path.GetFullPath(Path.Combine(search, path), folder))))
            if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("The SDK compiler reference is unavailable.", path);
    }
    private void ReleaseAuthoredWorkspace()
    {
        if (nativeContextGeneration is { } generation)
        {
            nativeContextGeneration = null;
            generation.Dispose();
            workspace = null; authoredWorkspace = null; authoredAnalyzerLoader = null; authoredResolution = null;
        }
        else
        {
            workspace?.Dispose(); workspace = null;
            authoredWorkspace?.Dispose(); authoredWorkspace = null;
            authoredAnalyzerLoader?.Dispose(); authoredAnalyzerLoader = null;
            if (authoredResolution is { } previous) { authoredResolution = null; RetireResolution(previous); }
        }
        foreach (var retired in retiredResolutions.Values.ToArray())
            try { retired.Dispose(); retiredResolutions.TryRemove(retired.Directory, out _); }
            catch (Exception error) { Console.Error.WriteLine("Compiler artifact cleanup failed: " + error.Message); }
    }
    private void RetireResolution(AuthoredProjectCompilationResolution resolution)
    {
        try { resolution.Dispose(); }
        catch (Exception error) { retiredResolutions.TryAdd(resolution.Directory, resolution); Console.Error.WriteLine("Compiler artifact cleanup failed: " + error.Message); }
    }

    private sealed class NativeAnalyzerLoader : AssemblyLoadContext, IAnalyzerAssemblyLoader, IDisposable
    {
        private readonly object locationGate = new();
        private readonly HashSet<string> locations = new(StringComparer.OrdinalIgnoreCase);
        public NativeAnalyzerLoader() : base(isCollectible: true) { }
        public void AddDependencyLocation(string fullPath) { lock (locationGate) locations.Add(Path.GetFullPath(fullPath)); }
        public Assembly LoadFromPath(string fullPath)
        {
            AddDependencyLocation(fullPath);
            var identity = AssemblyName.GetAssemblyName(fullPath);
            var existing = Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), identity));
            if (existing != null) return existing;
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LoadFromStream(stream);
        }
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
            if (shared != null) return shared;
            string[] roots;
            lock (locationGate) roots = locations.ToArray();
            var candidates = roots.Concat(roots.Select(path => Path.Combine(Path.GetDirectoryName(path)!, assemblyName.Name + ".dll"))).Distinct(StringComparer.OrdinalIgnoreCase);
            var path = candidates.FirstOrDefault(path => File.Exists(path) && AssemblyName.ReferenceMatchesDefinition(AssemblyName.GetAssemblyName(path), assemblyName));
            return path == null ? null : LoadFromPath(path);
        }
        public void Dispose() => Unload();
    }
}
