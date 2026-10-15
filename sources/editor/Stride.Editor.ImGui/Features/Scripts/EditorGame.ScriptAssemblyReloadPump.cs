using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ProjectWorkspace? assemblyWatchWorkspace;
    private readonly List<FileSystemWatcher> assemblyWatchers = [];
    private readonly ConcurrentDictionary<string, long> changedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Project, long At)> changedScriptSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PackageLoadedAssembly, string> assemblyOutputPaths = [];
    private readonly Dictionary<PackageLoadedAssembly, byte[]> observedAssemblyHashes = [];
    private readonly Dictionary<PackageLoadedAssembly, string> requestedAssemblyOutputs = [];
    private readonly HashSet<PackageLoadedAssembly> failedAssemblyReloads = [];
    private Task<AssemblyOutputSnapshot[]>? assemblySnapshotTask;
    private long assemblyWatchGeneration;
    private sealed record AssemblyOutputSnapshot(PackageLoadedAssembly Owner, string Path, byte[] Hash, Guid ModuleId);

    private void QueueScriptAssemblyReload(DesktopProjectBuildResult result)
    {
        if (workspace == null) return;
        foreach (var owner in workspace.Session.Packages.Where(package => !package.IsReadOnly).SelectMany(package => package.LoadedAssemblies))
            if (owner.ProjectReference?.Location != null
                && SamePath(owner.ProjectReference.Location.ToOSPath(), result.ProjectPath))
            {
                requestedAssemblyOutputs[owner] = result.TargetPath;
                if (failedAssemblyReloads.Remove(owner)) observedAssemblyHashes.Remove(owner);
            }
        // Dependency outputs remain tied to their actual loaded project target paths.
        foreach (var path in assemblyOutputPaths.Values) changedAssemblyPaths[path] = Environment.TickCount64;
    }

    private void PumpScriptAssemblyReload()
    {
        if (!ReferenceEquals(assemblyWatchWorkspace, workspace))
        {
            StopScriptAssemblyReload(); assemblyWatchWorkspace = workspace;
            if (workspace == null) return;
            foreach (var owner in workspace.Session.Packages.Where(package => !package.IsReadOnly).SelectMany(package => package.LoadedAssemblies))
            {
                if (owner.ProjectReference == null || string.IsNullOrWhiteSpace(owner.Path)) continue;
                var path = Path.GetFullPath(owner.Path); assemblyOutputPaths[owner] = path;
                changedAssemblyPaths[path] = Environment.TickCount64 - 650;
            }
            WatchAssemblyOutputDirectories();
        }
        if (workspace == null || authoring == null || sessionHistory == null || projectLoading != null
            || IsUserProjectBuilding || IsAuthoringPipelineUpdating || IsAssetPreviewUpdating
            || sessionHistory.Stack.TransactionInProgress || sessionHistory.Stack.RollInProgress || sessionHistory.IsCapturingOperations || sessionHistory.IsCompensating
            || closeRequested || closePromptOpen || exitApproved) return;
        if (assemblySnapshotTask?.IsCompleted == true)
        {
            var completed = assemblySnapshotTask; assemblySnapshotTask = null;
            try
            {
                var snapshots = completed.GetAwaiter().GetResult();
                foreach (var snapshot in snapshots)
                    if (!observedAssemblyHashes.ContainsKey(snapshot.Owner) && !failedAssemblyReloads.Contains(snapshot.Owner)
                        && snapshot.Owner.Assembly?.ManifestModule.ModuleVersionId == snapshot.ModuleId)
                        observedAssemblyHashes[snapshot.Owner] = snapshot.Hash;
                var changed = snapshots.Where(snapshot => !observedAssemblyHashes.TryGetValue(snapshot.Owner, out var before)
                    || !before.AsSpan().SequenceEqual(snapshot.Hash)).ToArray();
                if (changed.Length != 0)
                {
                    if (changed.Any(snapshot => !ReadAssemblyHash(snapshot.Path).AsSpan().SequenceEqual(snapshot.Hash)))
                    {
                        foreach (var snapshot in changed) changedAssemblyPaths[snapshot.Path] = Environment.TickCount64;
                        return;
                    }
                    var log = new LoggerResult();
                    try
                    {
                        ReloadScriptAssemblies(changed.ToDictionary(snapshot => snapshot.Owner, snapshot => snapshot.Path), log);
                        foreach (var snapshot in changed) failedAssemblyReloads.Remove(snapshot.Owner);
                        foreach (var snapshot in changed) assemblyOutputPaths[snapshot.Owner] = snapshot.Path;
                        WatchAssemblyOutputDirectories();
                        outputLog.Append("Build", LogMessageType.Info, "Reload assemblies", "Assemblies reloaded");
                    }
                    catch { foreach (var snapshot in changed) failedAssemblyReloads.Add(snapshot.Owner); throw; }
                    finally
                    {
                        foreach (var message in log.Messages) outputLog.Append("Build", message.Type, "Reload assemblies", message.ToString());
                        // A failed identical output is retried on another build/change, not every frame.
                        foreach (var snapshot in snapshots) observedAssemblyHashes[snapshot.Owner] = snapshot.Hash;
                    }
                }
            }
            catch (Exception error) { outputLog.Append("Build", LogMessageType.Error, "Reload assemblies", error.ToString()); }
        }
        if (assemblySnapshotTask != null) return;
        var now = Environment.TickCount64;
        if (requestedAssemblyOutputs.Count == 0 && !changedAssemblyPaths.Any(pair => now - pair.Value >= 650)) return;
        if (changedAssemblyPaths.Any(pair => now - pair.Value < 650)) return;
        changedAssemblyPaths.Clear();
        var candidates = assemblyOutputPaths.Concat(requestedAssemblyOutputs).GroupBy(pair => pair.Key).Select(group => group.Last()).ToArray();
        requestedAssemblyOutputs.Clear();
        var generation = Interlocked.Read(ref assemblyWatchGeneration);
        assemblySnapshotTask = Task.Run(() =>
        {
            var result = new List<AssemblyOutputSnapshot>();
            foreach (var pair in candidates)
            {
                try { result.Add(ReadAssemblyOutput(pair.Key, pair.Value)); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { if (Interlocked.Read(ref assemblyWatchGeneration) == generation) changedAssemblyPaths[pair.Value] = Environment.TickCount64; }
            }
            return result.ToArray();
        });
    }

    private static AssemblyOutputSnapshot ReadAssemblyOutput(PackageLoadedAssembly owner, string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = SHA256.HashData(stream); stream.Position = 0;
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var metadata = pe.GetMetadataReader();
        return new(owner, path, hash, metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
    }

    private static byte[] ReadAssemblyHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }
    private void WatchAssemblyOutputDirectories()
    {
        foreach (var watcher in assemblyWatchers) watcher.Dispose(); assemblyWatchers.Clear();
        var generation = Interlocked.Increment(ref assemblyWatchGeneration);
        var sourceProjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (workspace != null)
            foreach (var item in workspace.Session.Packages.SelectMany(package => package.Assets))
                if (item.Asset is SourceCodeAsset && item.Package.Container is SolutionProject project
                    && Path.GetExtension(item.FullPath.ToOSPath()).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    && workspace.Session.Packages.SelectMany(package => package.LoadedAssemblies).Any(owner => owner.ProjectReference?.Location != null
                        && SamePath(owner.ProjectReference.Location.ToOSPath(), project.FullPath.ToOSPath())))
                    sourceProjects[Path.GetFullPath(item.FullPath.ToOSPath())] = project.FullPath.ToOSPath();
        foreach (var group in assemblyOutputPaths.Values.Concat(sourceProjects.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(ExistingDirectory, StringComparer.OrdinalIgnoreCase).Where(group => group.Key != null))
        {
            var paths = group.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var watcher = new FileSystemWatcher(group.Key!) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(object? sender, FileSystemEventArgs args)
            {
                if (Interlocked.Read(ref assemblyWatchGeneration) == generation && paths.Contains(args.FullPath))
                {
                    if (sourceProjects.TryGetValue(args.FullPath, out var project)) changedScriptSourcePaths[args.FullPath] = (project, Environment.TickCount64);
                    else changedAssemblyPaths[args.FullPath] = Environment.TickCount64;
                }
            }
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
            watcher.Renamed += (_, args) => { Changed(null, args); Changed(null, new FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(args.OldFullPath)!, Path.GetFileName(args.OldFullPath))); };
            watcher.Error += (_, _) => { if (Interlocked.Read(ref assemblyWatchGeneration) == generation) foreach (var path in paths) Changed(null, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(path)!, Path.GetFileName(path))); };
            assemblyWatchers.Add(watcher); watcher.EnableRaisingEvents = true;
        }
        static string? ExistingDirectory(string path)
        {
            var directory = Path.GetDirectoryName(path);
            while (directory != null && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
            return directory;
        }
    }
    private void StopScriptAssemblyReload()
    {
        Interlocked.Increment(ref assemblyWatchGeneration);
        foreach (var watcher in assemblyWatchers) watcher.Dispose(); assemblyWatchers.Clear();
        if (assemblySnapshotTask != null) _ = assemblySnapshotTask.ContinueWith(task => { if (task.IsFaulted) _ = task.Exception; }, TaskScheduler.Default);
        assemblySnapshotTask = null; assemblyWatchWorkspace = null;
        changedAssemblyPaths.Clear(); assemblyOutputPaths.Clear(); observedAssemblyHashes.Clear(); requestedAssemblyOutputs.Clear();
        failedAssemblyReloads.Clear();
        changedScriptSourcePaths.Clear();
    }
}
