using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal sealed record ExternalAssetChange(AssetEntry Asset, byte[]? ContentHash)
{ public bool Missing => ContentHash == null; }

/// <summary>Native file notifications queue paths only. Hash comparison and authored conflict decisions stay on the editor actor.</summary>
internal sealed class AssetExternalChangeMonitor(Func<string, byte[]?> authoredBaseline) : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssetEntry> assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Modified, long Length)> stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExternalAssetChange> changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> watchers = [];
    private IReadOnlyList<ExternalAssetChange> changeSnapshot = [];
    private bool changeSnapshotDirty = true;
    private readonly Dictionary<string, long> retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private long nextPoll, generation;
    private int pollPosition;
    private string[] pollPaths = [];
    private Task<HashResult[]>? hashing;
    private CancellationTokenSource? hashCancellation;
    private readonly HashSet<string> initialBaselines = new(StringComparer.OrdinalIgnoreCase);
    private sealed record HashResult(string Path, long Generation, byte[]? Hash, bool Retry);
    public long ChangeVersion { get; private set; }
    public IReadOnlyList<ExternalAssetChange> Changes
    {
        get
        {
            if (changeSnapshotDirty)
            {
                changeSnapshot = changes.Values.OrderBy(change => change.Asset.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                changeSnapshotDirty = false;
            }
            return changeSnapshot;
        }
    }
    public void Configure(IReadOnlyList<AssetEntry> entries)
    {
        generation++; changeSnapshotDirty = true;
        hashCancellation?.Cancel();
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        var paths = entries.Select(asset => System.IO.Path.GetFullPath(asset.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in assets.Keys.Where(path => !paths.Contains(path)).ToArray()) { assets.Remove(path); observed.Remove(path); stamps.Remove(path); changes.Remove(path); initialBaselines.Remove(path); retryAfter.Remove(path); }
        foreach (var entry in entries)
        {
            var path = System.IO.Path.GetFullPath(entry.Path);
            if (assets.TryGetValue(path, out var previousOwner) && previousOwner.Id != entry.Id)
            { observed.Remove(path); stamps.Remove(path); changes.Remove(path); initialBaselines.Remove(path); }
            assets[path] = entry;
            if (!observed.ContainsKey(path))
            {
                var baseline = authoredBaseline(path);
                observed[path] = baseline == null || baseline.Length == 0 ? null : baseline;
                if (baseline == null) initialBaselines.Add(path);
            }
            dirty[path] = 0;
        }
        pollPaths = assets.Keys.ToArray(); pollPosition = 0; nextPoll = 0;
        foreach (var directory in paths.Select(System.IO.Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
            watcher.Renamed += (_, args) => { dirty[args.OldFullPath] = 0; dirty[args.FullPath] = 0; };
            watcher.Error += (_, _) => { foreach (var path in paths) dirty[path] = 0; };
            watcher.EnableRaisingEvents = true; watchers.Add(watcher);
        }
    }
    private void Changed(object sender, FileSystemEventArgs args) => dirty[args.FullPath] = 0;
    public void Tick()
    {
        if (hashing?.IsCompleted == true)
        {
            var completed = hashing; hashing = null;
            hashCancellation?.Dispose(); hashCancellation = null;
            foreach (var result in completed.GetAwaiter().GetResult())
            {
                if (result.Generation != generation || !assets.TryGetValue(result.Path, out var asset)) continue;
                if (result.Retry) { dirty[result.Path] = 0; retryAfter[result.Path] = Environment.TickCount64 + 250; continue; }
                retryAfter.Remove(result.Path);
                ApplyHash(asset, result.Path, result.Hash);
            }
        }
        if (Environment.TickCount64 >= nextPoll)
        {
            // Bound native metadata work per actor tick too; watchers still queue immediately.
            var limit = Math.Min(pollPaths.Length, pollPosition + 128);
            while (pollPosition < limit)
            {
                var path = pollPaths[pollPosition++];
                try
                {
                    var stamp = Stamp(path);
                    if (!stamps.TryGetValue(path, out var previous) || stamp != previous)
                    { dirty[path] = 0; stamps[path] = stamp; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { dirty[path] = 0; }
            }
            if (pollPosition >= pollPaths.Length) { pollPosition = 0; nextPoll = Environment.TickCount64 + 2000; }
        }
        if (hashing != null) return;
        var batch = new List<string>(16);
        foreach (var path in dirty.Keys)
        {
            if (retryAfter.TryGetValue(path, out var retry) && Environment.TickCount64 < retry) continue;
            if (!dirty.TryRemove(path, out _) || !assets.ContainsKey(path)) continue;
            batch.Add(path); if (batch.Count == 16) break;
        }
        if (batch.Count == 0) return;
        var currentGeneration = generation;
        hashCancellation = new(); var cancellation = hashCancellation.Token;
        hashing = Task.Run(async () =>
        {
            var results = new List<HashResult>(batch.Count);
            foreach (var path in batch)
            {
                if (cancellation.IsCancellationRequested) break;
                try
                {
                    var before = Stamp(path);
                    byte[]? hash = null;
                    if (File.Exists(path))
                    {
                        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        hash = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
                    }
                    results.Add(new(path, currentGeneration, hash, before != Stamp(path)));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { results.Add(new(path, currentGeneration, null, true)); }
            }
            return results.ToArray();
        });
    }
    private void ApplyHash(AssetEntry asset, string path, byte[]? hash)
    {
        var baseline = authoredBaseline(path);
        if (initialBaselines.Remove(path) && baseline == null)
        { observed[path] = hash; return; }
        if (baseline != null && EqualsHash(hash, baseline.Length == 0 ? null : baseline))
        { observed[path] = hash; if (changes.Remove(path)) changeSnapshotDirty = true; return; }
        if (!changes.ContainsKey(path) && EqualsHash(observed.GetValueOrDefault(path), hash)) return;
        if (changes.TryGetValue(path, out var current) && EqualsHash(current.ContentHash, hash)) return;
        observed[path] = hash; changes[path] = new(asset, hash); changeSnapshotDirty = true; ChangeVersion++;
    }
    public void Keep(ExternalAssetChange change) { if (changes.Remove(change.Asset.Path)) changeSnapshotDirty = true; }
    public void Reloaded(ExternalAssetChange change)
    { if (changes.Remove(change.Asset.Path)) changeSnapshotDirty = true; observed[change.Asset.Path] = change.ContentHash; }
    private static bool EqualsHash(byte[]? left, byte[]? right) => left == null ? right == null : right != null && left.AsSpan().SequenceEqual(right);
    private static (long, long) Stamp(string path)
    {
        var file = new FileInfo(path); return file.Exists ? (file.LastWriteTimeUtc.Ticks, file.Length) : (0, 0);
    }
    public void Dispose()
    {
        generation++; hashCancellation?.Cancel();
        if (hashing is { } pending)
        {
            var cancellation = hashCancellation; hashing = null; hashCancellation = null;
            _ = pending.ContinueWith(task => { _ = task.Exception; cancellation?.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else { hashCancellation?.Dispose(); hashCancellation = null; }
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        assets.Clear(); observed.Clear(); stamps.Clear(); changes.Clear(); initialBaselines.Clear(); retryAfter.Clear(); dirty.Clear(); pollPaths = []; changeSnapshot = []; changeSnapshotDirty = true;
    }
}
