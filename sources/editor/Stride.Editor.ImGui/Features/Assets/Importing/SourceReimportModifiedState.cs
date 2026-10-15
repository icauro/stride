using System.Collections.Concurrent;
using Stride.Core.Assets;
using Stride.Core.Storage;

namespace Stride.Editor.ImGui;

/// <summary>Native content hashes cached behind actual source-file notifications.</summary>
internal sealed class SourceReimportModifiedState : IDisposable
{
    private sealed record ScanResult(HashSet<AssetId> Modified, Dictionary<string, ObjectId> Hashes);
    private Task<ScanResult>? task;
    private CancellationTokenSource? cancellation;
    private object? scope;
    private long nextScan;
    private int generation;
    private readonly HashSet<AssetId> modified = [];
    private readonly Dictionary<string, ObjectId> hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (FileSystemWatcher Watcher, bool Recursive)> watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string?> changes = new();
    internal IReadOnlySet<AssetId> Modified => modified;
    internal bool NeedsSnapshot(object currentScope)
    {
        if (!ReferenceEquals(scope, currentScope)) { Clear(); scope = currentScope; }
        if (task is { IsCompleted: true } completed)
        {
            task = null;
            try
            {
                var result = completed.GetAwaiter().GetResult(); modified.Clear(); modified.UnionWith(result.Modified);
                hashes.Clear(); foreach (var pair in result.Hashes) hashes.Add(pair.Key, pair.Value);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { hashes.Clear(); modified.Clear(); }
            finally { cancellation?.Dispose(); cancellation = null; }
        }
        return task == null && (Environment.TickCount64 >= nextScan || !changes.IsEmpty);
    }
    internal void Start(SourceReimportCertificate[] certificates)
    {
        nextScan = Environment.TickCount64 + 2000;
        var plan = new ThumbnailSourceWatchPlan();
        foreach (var certificate in certificates) foreach (var source in certificate.Sources) plan.Add(source, certificate.Id);
        foreach (var path in watchers.Keys.Where(path => !plan.Directories.ContainsKey(path)).ToArray())
        { watchers[path].Watcher.Dispose(); watchers.Remove(path); }
        var currentGeneration = generation;
        foreach (var directory in plan.Directories)
        {
            if (watchers.TryGetValue(directory.Key, out var existing) && existing.Recursive == directory.Value) continue;
            if (existing.Watcher != null) { existing.Watcher.Dispose(); watchers.Remove(directory.Key); }
            FileSystemWatcher? pendingWatcher = null;
            try
            {
                var watcher = pendingWatcher = new FileSystemWatcher(directory.Key)
                {
                    IncludeSubdirectories = directory.Value,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                void Changed(object _, FileSystemEventArgs e) { if (currentGeneration == Volatile.Read(ref generation) && plan.Affected(e.FullPath).Any()) changes.Enqueue(e.FullPath); }
                watcher.Created += Changed; watcher.Changed += Changed; watcher.Deleted += Changed;
                watcher.Renamed += (_, e) => { Changed(watcher, e); if (currentGeneration == Volatile.Read(ref generation) && plan.Affected(e.OldFullPath).Any()) changes.Enqueue(e.OldFullPath); };
                watcher.Error += (_, _) => { if (currentGeneration == Volatile.Read(ref generation)) changes.Enqueue(null); };
                watcher.EnableRaisingEvents = true; watchers.Add(directory.Key, (watcher, directory.Value)); pendingWatcher = null;
            }
            catch (IOException) { hashes.Clear(); }
            catch (UnauthorizedAccessException) { hashes.Clear(); }
            finally { pendingWatcher?.Dispose(); }
        }
        var cached = new Dictionary<string, ObjectId>(hashes, StringComparer.OrdinalIgnoreCase);
        while (changes.TryDequeue(out var changed))
        {
            if (changed == null) { cached.Clear(); continue; }
            var full = Path.GetFullPath(changed); var prefix = Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
            foreach (var path in cached.Keys.Where(path => path.Equals(full, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray()) cached.Remove(path);
        }
        foreach (var path in cached.Keys.Where(path => !plan.Files.ContainsKey(path)).ToArray()) cached.Remove(path);
        cancellation = new(); var token = cancellation.Token;
        task = Task.Run(() =>
        {
            var result = new HashSet<AssetId>();
            foreach (var certificate in certificates)
            {
                token.ThrowIfCancellationRequested();
                foreach (var source in certificate.Sources)
                {
                    if (!cached.TryGetValue(source, out var hash))
                    {
                        var cacheable = true;
                        try { hash = File.Exists(source) ? AssetSourceImport.CaptureSources([source], token).ContentHashes[source] : ObjectId.Empty; }
                        catch (IOException) { hash = ObjectId.Empty; cacheable = false; }
                        catch (UnauthorizedAccessException) { hash = ObjectId.Empty; cacheable = false; }
                        if (cacheable) cached[source] = hash;
                    }
                    if (!certificate.SavedHashes.TryGetValue(source, out var saved) || saved != hash) result.Add(certificate.Id);
                }
            }
            return new ScanResult(result, cached);
        }, token);
    }
    internal void Clear()
    {
        Interlocked.Increment(ref generation);
        foreach (var watcher in watchers.Values) watcher.Watcher.Dispose(); watchers.Clear();
        while (changes.TryDequeue(out _)) { }
        var previous = task; var cancel = cancellation; task = null; cancellation = null;
        scope = null; modified.Clear(); hashes.Clear(); nextScan = 0; cancel?.Cancel();
        if (previous == null) { cancel?.Dispose(); return; }
        _ = previous.ContinueWith(completed =>
        {
            try { if (completed.IsFaulted) _ = completed.Exception; }
            finally { cancel?.Dispose(); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public void Dispose() => Clear();
}
