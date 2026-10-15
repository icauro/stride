using Stride.Core.Assets;
using System.Xml;
using System.Security.Cryptography;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using NuGet.ProjectModel;
using MicrosoftProjectCollection = Microsoft.Build.Evaluation.ProjectCollection;
using CoreLogger = Stride.Core.Diagnostics.ILogger;

namespace Stride.Editor.ImGui;

internal static partial class AuthoredProjectCompilationResolver
{
    private static DependencyGraphSpec GenerateNativeRestoreGraph(ProjectGraph graph, IReadOnlyDictionary<string, ProjectRootElement> roots,
        MicrosoftProjectCollection collection, string directory, Dictionary<string, AuthoredProjectInput> inputs, CoreLogger logger, CancellationToken cancellation)
    {
        var entries = new List<(string Include, KeyValuePair<string, string>[] Metadata)>();
        var projectEntries = new Dictionary<(string Type, string Project), KeyValuePair<string, string>[]>();
        using var manager = new BuildManager();
        manager.BeginBuild(new BuildParameters(collection) { Loggers = [new ResolutionBuildLogger(logger)], DisableInProcNode = true, EnableNodeReuse = false });
        using var registration = cancellation.Register(manager.CancelAllSubmissions);
        try
        {
            foreach (var node in graph.ProjectNodesTopologicallySorted)
            {
                cancellation.ThrowIfCancellationRequested();
                var original = node.ProjectInstance;
                if (string.Equals(original.GetPropertyValue("IsCrossTargetingBuild"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                var authoredRoot = roots.GetValueOrDefault(original.FullPath) ?? ProjectRootElement.Open(original.FullPath, collection);
                var root = CloneNativeRoot(authoredRoot, collection);
                var target = root.AddTarget("_StrideImGuiNativeRestoreEntries_" + Guid.NewGuid().ToString("N"));
                // Invoke the SDK's own spec/selected-framework collectors in
                // this exact evaluated instance. The stock graph driver uses
                // MSBuild self-requests and would read the saved XML again.
                target.DependsOnTargets = "_GenerateRestoreGraphProjectEntry;_GetRestoreProjectStyle;_GenerateRestoreProjectSpec;_GenerateProjectRestoreGraphPerFramework";
                target.Returns = "@(_RestoreGraphEntry)";
                var properties = original.GlobalProperties.ToDictionary(pair => pair.Key, pair => pair.Value);
                properties["_StrideImGuiNativeRestoreRequest"] = Guid.NewGuid().ToString("N");
                var instance = new ProjectInstance(root, properties, original.ToolsVersion, collection);
                var result = manager.BuildRequest(new BuildRequestData(instance, [target.Name], null, BuildRequestDataFlags.ProvideProjectStateAfterBuild));
                if (result.OverallResult != BuildResultCode.Success) throw new InvalidOperationException("MSBuild could not collect the authored native restore inputs.");
                var state = result.ProjectStateAfterBuild ?? throw new InvalidOperationException("MSBuild returned no authored restore project state.");
                foreach (var path in state.ImportPaths)
                    if (File.Exists(path) && !Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) CaptureInput(inputs, path);
                foreach (var item in state.GetItems("_RestoreGraphEntry"))
                {
                    var metadata = item.Metadata.Select(metadata => new KeyValuePair<string, string>(metadata.Name, metadata.EvaluatedValue)).ToArray();
                    var values = metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    var type = values.GetValueOrDefault("Type") ?? "";
                    if (type is "ProjectSpec" or "RestoreSpec")
                    {
                        var key = (type, (values.GetValueOrDefault("ProjectUniqueName") ?? "").ToUpperInvariant());
                        var ordered = metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                        if (projectEntries.TryGetValue(key, out var prior))
                        {
                            if (!ordered.SequenceEqual(prior)) throw new InvalidOperationException("The SDK emitted conflicting project restore specifications for selected target frameworks.");
                            continue;
                        }
                        projectEntries.Add(key, ordered);
                    }
                    entries.Add((item.EvaluatedInclude, metadata));
                }
            }
            if (entries.Count == 0) throw new InvalidOperationException("The SDK returned no native restore graph entries.");
            var host = graph.EntryPointNodes.First().ProjectInstance;
            var hostRoot = roots.GetValueOrDefault(host.FullPath) ?? ProjectRootElement.Open(host.FullPath, collection);
            var writerRoot = CloneNativeRoot(hostRoot, collection);
            var writer = writerRoot.AddTarget("_StrideImGuiWriteNativeRestoreGraph_" + Guid.NewGuid().ToString("N"));
            var removeDuplicates = writer.AddTask("RemoveDuplicates");
            removeDuplicates.SetParameter("Inputs", "@(_RestoreGraphEntry)");
            removeDuplicates.AddOutputItem("Filtered", "_RestoreGraphEntryFiltered");
            var write = writer.AddTask("WriteRestoreGraphTask");
            write.SetParameter("RestoreGraphItems", "@(_RestoreGraphEntryFiltered)");
            write.SetParameter("RestoreGraphOutputPath", "$(RestoreGraphOutputPath)");
            write.SetParameter("RestoreRecursive", "false");
            var graphPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".dg.json");
            var writerProperties = host.GlobalProperties.ToDictionary(pair => pair.Key, pair => pair.Value);
            writerProperties["_StrideImGuiNativeRestoreRequest"] = Guid.NewGuid().ToString("N");
            writerProperties["RestoreGraphOutputPath"] = graphPath;
            var writerInstance = new ProjectInstance(writerRoot, writerProperties, host.ToolsVersion, collection);
            foreach (var entry in entries) writerInstance.AddItem("_RestoreGraphEntry", entry.Include, entry.Metadata);
            cancellation.ThrowIfCancellationRequested();
            var written = manager.BuildRequest(new BuildRequestData(writerInstance, [writer.Name], null, BuildRequestDataFlags.ProvideProjectStateAfterBuild));
            if (written.OverallResult != BuildResultCode.Success || !File.Exists(graphPath)) throw new InvalidOperationException("MSBuild could not write its authored native restore graph.");
            var generated = DependencyGraphSpec.Load(graphPath);
            foreach (var spec in generated.Projects)
            {
                spec.RestoreMetadata.OutputPath = ProjectArtifactDirectory(directory, spec.RestoreMetadata.ProjectUniqueName);
                spec.RestoreMetadata.CacheFilePath = Path.Combine(spec.RestoreMetadata.OutputPath, "project.nuget.cache");
                var locks = spec.RestoreMetadata.RestoreLockProperties ?? new RestoreLockProperties();
                var originalLock = Path.GetFullPath(string.IsNullOrEmpty(locks.NuGetLockFilePath) ? PackagesLockFileFormat.LockFileName : locks.NuGetLockFilePath,
                    Path.GetDirectoryName(spec.RestoreMetadata.ProjectPath)!);
                var privateLock = Path.Combine(spec.RestoreMetadata.OutputPath, PackagesLockFileFormat.LockFileName);
                if (File.Exists(originalLock))
                {
                    CaptureInput(inputs, originalLock);
                    File.WriteAllBytes(privateLock, File.ReadAllBytes(originalLock));
                }
                spec.RestoreMetadata.RestoreLockProperties = new RestoreLockProperties(locks.RestorePackagesWithLockFile, privateLock, locks.RestoreLockedMode);
                foreach (var path in spec.RestoreMetadata.ConfigFilePaths) if (File.Exists(path)) CaptureInput(inputs, path);
            }
            return generated;
        }
        finally { manager.EndBuild(); }
    }
    private static ProjectRootElement CloneNativeRoot(ProjectRootElement authored, MicrosoftProjectCollection collection)
    {
        using var reader = XmlReader.Create(new StringReader(authored.RawXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = ProjectRootElement.Create(reader, collection, preserveFormatting: true);
        root.FullPath = authored.FullPath;
        return root;
    }
    private static ProjectRootElement ReadOriginalRoot(string path, MicrosoftProjectCollection collection, Dictionary<string, AuthoredProjectInput> inputs)
    {
        byte[] bytes;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var memory = new MemoryStream()) { file.CopyTo(memory); bytes = memory.ToArray(); }
        var captured = new AuthoredProjectInput(Path.GetFullPath(path), SHA256.HashData(bytes));
        lock (inputs)
        {
            if (inputs.TryGetValue(captured.Path, out var previous) && !previous.Hash.AsSpan().SequenceEqual(captured.Hash))
                throw new InvalidOperationException("A referenced project changed during native graph evaluation.");
            inputs[captured.Path] = captured;
        }
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = ProjectRootElement.Create(reader, collection, preserveFormatting: true); root.FullPath = captured.Path;
        return root;
    }
}
