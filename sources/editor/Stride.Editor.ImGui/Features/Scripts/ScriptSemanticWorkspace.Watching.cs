using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ScriptSemanticWorkspace
{
    private readonly object watchGate = new();
    private readonly HashSet<string> watchKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> projectWatchRoots = new(StringComparer.OrdinalIgnoreCase);
    private long lastInvalidation;
    private bool watchRecoveryRequired;
    private long watchRecoveryAt;
    private int watchGeneration;
    public bool IsSettled => Interlocked.Read(ref lastInvalidation) == 0 || Stopwatch.GetElapsedTime(Interlocked.Read(ref lastInvalidation)).TotalMilliseconds >= 300;

    private void ObserveProjectOrigin(string path)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        if (ObserveDirectory(directory, true, ["*.cs", "*.csproj", "*.props", "*.targets", "*.dll", "project.assets.json", ".editorconfig", "*.globalconfig"]))
            lock (watchGate) projectWatchRoots.Add(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        for (var parent = directory; parent != null; parent = Path.GetDirectoryName(parent))
            ObserveDirectory(parent, false, ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config", ".editorconfig", "*.globalconfig"]);
    }
    private void ObserveFile(string path)
    {
        path = Path.GetFullPath(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase) || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            lock (watchGate) if (projectWatchRoots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase))) return;
        var directory = Path.GetDirectoryName(path)!;
        var existing = directory;
        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new IOException("The project import has no existing parent directory.");
        string[] filters = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ? ["*.cs"]
            : extension.Equals(".props", StringComparison.OrdinalIgnoreCase) || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ? ["*.props", "*.targets"]
            : extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ? ["*.dll", "*.exe"] : [Path.GetFileName(path)];
        ObserveDirectory(existing, !string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase), filters);
    }
    private bool ObserveDirectory(string directory, bool recursive, string[] filters)
    {
        if (!Directory.Exists(directory)) return false;
        var key = directory + "\n" + recursive + "\n" + string.Join('|', filters);
        lock (watchGate)
        {
            if (disposed) return false;
            if (!watchKeys.Add(key)) return true;
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = recursive, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName };
                watcher.Filters.Clear(); foreach (var filter in filters) watcher.Filters.Add(filter);
                var generation = watchGeneration;
                void ChangedCurrent(object sender, FileSystemEventArgs args)
                { if (generation == Volatile.Read(ref watchGeneration)) Invalidate(); }
                watcher.Changed += ChangedCurrent; watcher.Created += ChangedCurrent; watcher.Deleted += ChangedCurrent;
                watcher.Renamed += (_, _) => { if (generation == Volatile.Read(ref watchGeneration)) Invalidate(); };
                watcher.Error += (_, _) => { if (generation == Volatile.Read(ref watchGeneration)) WatcherFailed(); };
                watcher.EnableRaisingEvents = true; watchers.Add(watcher); return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { watcher?.Dispose(); watchKeys.Remove(key); watchRecoveryRequired = true; watchRecoveryAt = Stopwatch.GetTimestamp(); Console.Error.WriteLine("Script project observation failed: " + error.Message); return false; }
        }
    }
    private void WatcherFailed()
    {
        lock (watchGate) { if (disposed) return; watchRecoveryRequired = true; watchRecoveryAt = Stopwatch.GetTimestamp(); }
        Invalidate();
    }
    private void ScheduleWatchRecovery()
    {
        lock (watchGate)
        {
            if (!watchRecoveryRequired || disposed || Stopwatch.GetElapsedTime(watchRecoveryAt).TotalSeconds < 2) return;
            watchRecoveryAt = Stopwatch.GetTimestamp();
            Invalidate();
        }
    }
    private void RecoverWatchers()
    {
        lock (watchGate)
        {
            if (!watchRecoveryRequired || disposed) return;
            watchRecoveryRequired = false;
            Interlocked.Increment(ref watchGeneration);
            foreach (var watcher in watchers) watcher.Dispose();
            watchers.Clear(); watchKeys.Clear(); projectWatchRoots.Clear();
        }
        ObserveProjectOrigin(projectPath);
    }
    private void Invalidate()
    {
        lock (watchGate)
        {
            if (disposed) return;
            var now = Stopwatch.GetTimestamp();
            if (lastInvalidation == 0 || Stopwatch.GetElapsedTime(lastInvalidation, now).TotalMilliseconds >= 300) Interlocked.Increment(ref epoch);
            Interlocked.Exchange(ref lastInvalidation, now);
        }
    }
    private void Changed(object sender, FileSystemEventArgs args) => Invalidate();
    private void Renamed(object sender, RenamedEventArgs args) => Invalidate();
    private async Task WaitForQuiet(CancellationToken cancellation)
    {
        while (!IsSettled) await Task.Delay(100, cancellation).ConfigureAwait(false);
    }
    private void ObserveLiteralImports(string project)
    {
        var pending = new Stack<string>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); pending.Push(project);
        while (pending.TryPop(out var path))
        {
            path = Path.GetFullPath(path);
            if (!visited.Add(path)) continue;
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) ObserveProjectOrigin(path); else ObserveFile(path);
            if (!File.Exists(path)) continue;
            try
            {
                var xml = XDocument.Load(path); var directory = Path.GetDirectoryName(path)!;
                foreach (var element in xml.Descendants().Where(element => element.Name.LocalName is "Import" or "ProjectReference"))
                {
                    var value = element.Attribute(element.Name.LocalName == "Import" ? "Project" : "Include")?.Value;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    value = value.Replace("$(MSBuildThisFileDirectory)", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    if (value.Contains("$(", StringComparison.Ordinal) || value.IndexOfAny(['*', '?', ';']) >= 0) continue;
                    pending.Push(Path.GetFullPath(value, directory));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
            { Console.Error.WriteLine("Script import observation failed: " + error.Message); }
        }
    }
    private void InstallWatchers(Solution solution)
    {
        // Initial watches stay alive after failed loads and throughout context replacements.
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is { } projectFile) ObserveProjectOrigin(projectFile);
            foreach (var document in project.Documents.Cast<TextDocument>().Concat(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments))
                if (document.FilePath is { } path) ObserveFile(path);
            foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
                if (reference.FilePath is { } path) ObserveFile(path);
            foreach (var reference in project.AnalyzerReferences)
                if (reference.FullPath is { Length: > 0 } path) ObserveFile(path);
        }
        foreach (var reference in solution.AnalyzerReferences)
            if (reference.FullPath is { Length: > 0 } path) ObserveFile(path);
    }

    private void ObserveEvaluatedImportPatterns(string path, string selectedFramework)
    {
        var snapshot = Volatile.Read(ref authoredProjects).SingleOrDefault(snapshot => string.Equals(snapshot.Path, path, StringComparison.OrdinalIgnoreCase));
        var bytes = snapshot == null ? File.ReadAllBytes(path) : System.Text.Encoding.UTF8.GetBytes(snapshot.Xml);
        using var properties = new AuthoredProjectProperties(bytes, path);
        var contexts = properties.EvaluateDependencyContexts(configuration);
        var selected = contexts.Where(context => selectedFramework.Length == 0 || context.Framework == selectedFramework).ToArray();
        foreach (var context in selected.Length == 0 ? contexts : selected)
        {
            var inputs = properties.EvaluateBuildInputs(configuration, context.Framework);
            foreach (var input in inputs.Paths) ObserveFile(input);
            foreach (var root in inputs.ImportWatchDirectories ?? [])
            {
                var existing = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new IOException("An evaluated import pattern has no existing parent.");
                ObserveDirectory(existing, true, ["*"]);
            }
        }
    }
}
