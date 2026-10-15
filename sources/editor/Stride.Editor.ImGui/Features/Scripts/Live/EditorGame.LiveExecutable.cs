using System.Collections.Concurrent;
using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // The executable is a native launch target, not a fabricated package assembly.
    private DesktopProjectBuildResult? liveExecutableBuild;
    private LiveScriptAssembly? liveExecutableAssembly;
    private readonly List<FileSystemWatcher> liveExecutableWatchers = [];
    private readonly ConcurrentDictionary<string, long> liveExecutableChanges = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> liveExecutableSources = new(StringComparer.OrdinalIgnoreCase);
    private int liveExecutableWatchGeneration;
    private HashSet<string> liveExecutableInputs = new(StringComparer.OrdinalIgnoreCase);
    private sealed record LiveExecutableInput(string Path, byte[]? Hash);
    private sealed record LiveExecutableProjectRevision(string Path, long Revision);
    private sealed record LiveExecutableInputCertificate(DesktopProjectBuildResult Build, long Revision, int Generation,
        LiveExecutableInput[] Inputs, LiveExecutableProjectRevision[] Projects, long CapturedAt);
    private Task<LiveExecutableInputCertificate>? liveExecutableInputTask;
    private LiveExecutableInputCertificate? liveExecutableInputCertificate;
    private long liveExecutableWatchFailure;
    private long lastLiveInputRevisionCheck;
    private string[] liveExecutableImportRoots = [];

    private void InstallLiveExecutable(DesktopProjectBuildResult build)
    {
        var captured = LiveScriptSession.Capture(build.ProjectPath, build.TargetPath);
        liveExecutableBuild = build;
        liveExecutableAssembly = captured;
        WatchLiveExecutable();
    }

    private void WatchLiveExecutable()
    {
        var generation = Interlocked.Increment(ref liveExecutableWatchGeneration);
        foreach (var watcher in liveExecutableWatchers) watcher.Dispose();
        liveExecutableWatchers.Clear();
        liveExecutableSources.Clear();
        liveExecutableInputCertificate = null;
        var build = liveExecutableBuild;
        if (build == null || workspace == null || sessionHistory == null) return;
        var project = workspace.Projects.Single(project => SamePath(project.FullPath.ToOSPath(), build.ProjectPath));
        var owner = GetProjectBuildSettingsDocument(project, sessionHistory);
        var context = owner.EvaluateDependencyContexts(build.Configuration)
            .Single(context => context.Framework == build.Framework);
        var initialInputs = owner.EvaluateBuildInputs(build.Configuration, build.Framework);
        var inputPaths = new HashSet<string>(initialInputs.Paths, StringComparer.OrdinalIgnoreCase);
        var importRoots = new HashSet<string>(initialInputs.ImportWatchDirectories ?? [], StringComparer.OrdinalIgnoreCase);
        var projectRevisions = new List<LiveExecutableProjectRevision> { new(owner.Path, owner.AuthoringRevision) };
        var dependencies = new Queue<string>(context.ProjectReferences);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { build.ProjectPath };
        while (dependencies.TryDequeue(out var dependency))
        {
            dependency = Path.GetFullPath(dependency);
            if (!visited.Add(dependency)) continue;
            var reference = workspace.Projects.SingleOrDefault(candidate => SamePath(candidate.FullPath.ToOSPath(), dependency));
            if (reference != null)
            {
                var retained = GetProjectBuildSettingsDocument(reference, sessionHistory);
                projectRevisions.Add(new(retained.Path, retained.AuthoringRevision));
                foreach (var evaluated in retained.EvaluateDependencyContexts(build.Configuration))
                {
                    var inputs = retained.EvaluateBuildInputs(build.Configuration, evaluated.Framework);
                    inputPaths.UnionWith(inputs.Paths); importRoots.UnionWith(inputs.ImportWatchDirectories ?? []);
                    foreach (var path in evaluated.ProjectReferences) dependencies.Enqueue(path);
                }
            }
            else
            {
                using var external = new AuthoredProjectProperties(File.ReadAllBytes(dependency), dependency);
                foreach (var evaluated in external.EvaluateDependencyContexts(build.Configuration))
                {
                    var inputs = external.EvaluateBuildInputs(build.Configuration, evaluated.Framework);
                    inputPaths.UnionWith(inputs.Paths); importRoots.UnionWith(inputs.ImportWatchDirectories ?? []);
                    foreach (var path in evaluated.ProjectReferences) dependencies.Enqueue(path);
                }
            }
        }
        liveExecutableInputs = new(inputPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        liveExecutableImportRoots = importRoots.Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        var paths = liveExecutableInputs.ToArray(); var revision = owner.AuthoringRevision;
        var revisions = projectRevisions.ToArray();
        ObserveLiveExecutableInputTask(liveExecutableInputTask);
        liveExecutableInputTask = Task.Run(() =>
        {
            var captured = paths.Select(path => new LiveExecutableInput(path, File.Exists(path) ? ReadLiveInputHash(path) : null)).ToArray();
            return new LiveExecutableInputCertificate(build, revision, generation, captured, revisions, Environment.TickCount64);
        });
        liveExecutableSources.UnionWith(context.CompilePaths.Select(Path.GetFullPath));
        var directories = context.CompilePaths.Concat(inputPaths).Select(path => ExistingLiveInputDirectory(path))
            .Concat(importRoots.Select(root => ExistingLiveInputDirectory(Path.Combine(root, "__import_observation__"))))
            .Append(Path.GetDirectoryName(build.ProjectPath)).Append(Path.GetDirectoryName(build.TargetPath))
            .Where(directory => directory != null && Directory.Exists(directory)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            var watcher = new FileSystemWatcher(directory!) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(string path)
            {
                if (generation == Volatile.Read(ref liveExecutableWatchGeneration))
                    liveExecutableChanges[Path.GetFullPath(path)] = Environment.TickCount64;
            }
            watcher.Changed += (_, args) => Changed(args.FullPath);
            watcher.Created += (_, args) => Changed(args.FullPath);
            watcher.Deleted += (_, args) => Changed(args.FullPath);
            watcher.Renamed += (_, args) => { Changed(args.OldFullPath); Changed(args.FullPath); };
            watcher.Error += (_, _) =>
            {
                if (generation == Volatile.Read(ref liveExecutableWatchGeneration))
                    Interlocked.Exchange(ref liveExecutableWatchFailure, Environment.TickCount64);
            };
            liveExecutableWatchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
    }

    private void PumpLiveExecutableChanges()
    {
        var build = liveExecutableBuild;
        if (build == null || workspace == null || sessionHistory == null) return;
        if (liveExecutableInputTask == null && liveExecutableInputCertificate is { } current && Environment.TickCount64 - lastLiveInputRevisionCheck >= 650)
        {
            lastLiveInputRevisionCheck = Environment.TickCount64;
            var retained = authoring!.PublishedStates.Select(state => state.Document).OfType<ProjectBuildSettingsDocument>().ToArray();
            if (current.Projects.Any(expected => !retained.Any(owner => SamePath(owner.Path, expected.Path) && owner.AuthoringRevision == expected.Revision))
                && Interlocked.Read(ref liveExecutableWatchFailure) == 0)
                Interlocked.Exchange(ref liveExecutableWatchFailure, Environment.TickCount64);
        }
        if (liveExecutableInputTask?.IsCompleted == true)
        {
            var completed = liveExecutableInputTask; liveExecutableInputTask = null;
            try
            {
                var certificate = completed.GetAwaiter().GetResult();
                var project = workspace.Projects.Single(project => SamePath(project.FullPath.ToOSPath(), build.ProjectPath));
                if (ReferenceEquals(certificate.Build, build) && certificate.Generation == liveExecutableWatchGeneration
                    && GetProjectBuildSettingsDocument(project, sessionHistory).AuthoringRevision == certificate.Revision
                    && certificate.Projects.All(expected => authoring!.PublishedStates.Select(state => state.Document)
                        .OfType<ProjectBuildSettingsDocument>().Any(retained => SamePath(retained.Path, expected.Path) && retained.AuthoringRevision == expected.Revision)))
                    liveExecutableInputCertificate = certificate;
                else Interlocked.Exchange(ref liveExecutableWatchFailure, Environment.TickCount64);
            }
            catch (IOException) { Interlocked.Exchange(ref liveExecutableWatchFailure, Environment.TickCount64); }
            catch (Exception error) { outputLog.Append("Live scripting", LogMessageType.Error, "Live scripting", error.ToString()); }
        }
        var failureAt = Interlocked.Read(ref liveExecutableWatchFailure);
        if (failureAt != 0 && Environment.TickCount64 - failureAt >= 650)
        {
            Interlocked.Exchange(ref liveExecutableWatchFailure, 0);
            try { WatchLiveExecutable(); QueueScriptSourceProject(build.ProjectPath); }
            catch (Exception error)
            {
                Interlocked.Exchange(ref liveExecutableWatchFailure, Environment.TickCount64);
                outputLog.Append("Live scripting", LogMessageType.Error, "Live scripting", error.ToString());
            }
        }
        foreach (var change in liveExecutableChanges.Where(pair => Environment.TickCount64 - pair.Value >= 650).ToArray())
        {
            if (!liveExecutableChanges.TryRemove(change.Key, out _)) continue;
            try
            {
                if (SamePath(change.Key, build.TargetPath))
                {
                    if (File.Exists(change.Key)) liveExecutableAssembly = LiveScriptSession.Capture(build.ProjectPath, build.TargetPath);
                    continue;
                }
                var source = liveExecutableSources.Contains(change.Key);
                if (!source && Path.GetExtension(change.Key).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var project = workspace.Projects.Single(project => SamePath(project.FullPath.ToOSPath(), build.ProjectPath));
                    var relative = Path.GetRelativePath(Path.GetDirectoryName(build.ProjectPath)!, change.Key);
                    if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                        source = GetProjectBuildSettingsDocument(project, sessionHistory).IsProjectItemIncluded("Compile", relative, build.Configuration, build.Framework);
                }
                var imported = liveExecutableImportRoots.Any(root => change.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                if (source || imported || liveExecutableInputs.Contains(change.Key) || SamePath(change.Key, build.ProjectPath))
                {
                    var baseline = liveExecutableInputCertificate?.Inputs.SingleOrDefault(input => SamePath(input.Path, change.Key));
                    var hash = File.Exists(change.Key) ? ReadLiveInputHash(change.Key) : null;
                    if (baseline == null || change.Value <= liveExecutableInputCertificate!.CapturedAt || (baseline.Hash == null) != (hash == null)
                        || baseline.Hash != null && hash != null && !baseline.Hash.AsSpan().SequenceEqual(hash))
                        QueueScriptSourceProject(build.ProjectPath);
                    if (imported || SamePath(change.Key, build.ProjectPath) || Path.GetExtension(change.Key) is ".props" or ".targets")
                        Interlocked.Exchange(ref liveExecutableWatchFailure, Environment.TickCount64);
                }
            }
            catch (IOException)
            {
                // Native compiler/IDE writers can still hold the real output.
                // Retry a stable snapshot rather than dropping its notification.
                liveExecutableChanges[change.Key] = Environment.TickCount64;
            }
            catch (Exception error) { outputLog.Append("Live scripting", LogMessageType.Error, "Live scripting", error.ToString()); }
        }
    }

    private void ClearLiveExecutable()
    {
        Interlocked.Increment(ref liveExecutableWatchGeneration);
        foreach (var watcher in liveExecutableWatchers) watcher.Dispose();
        liveExecutableWatchers.Clear(); liveExecutableChanges.Clear(); liveExecutableSources.Clear();
        liveExecutableInputs.Clear(); liveExecutableInputCertificate = null;
        liveExecutableImportRoots = [];
        ObserveLiveExecutableInputTask(liveExecutableInputTask); liveExecutableInputTask = null;
        Interlocked.Exchange(ref liveExecutableWatchFailure, 0);
        lastLiveInputRevisionCheck = 0;
        liveExecutableBuild = null; liveExecutableAssembly = null;
    }

    private static byte[] ReadLiveInputHash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(file);
    }
    private static string? ExistingLiveInputDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        while (directory != null && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
        return directory;
    }
    private static void ObserveLiveExecutableInputTask(Task? task)
    {
        if (task != null) _ = task.ContinueWith(completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}
