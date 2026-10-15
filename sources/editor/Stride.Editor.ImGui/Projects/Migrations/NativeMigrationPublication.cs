using System.Security.Cryptography;
using System.Text.Json;

namespace Stride.Editor.ImGui;

/// <summary>Publishes native staged bytes after graph release, with exact file compensation.</summary>
internal static class NativeMigrationPublication
{
    internal sealed record Result(string OpenPath, string? BackupDirectory, int ChangedFiles, DateTimeOffset PublishedAt);
    private sealed record PreparedFile(PreparedNativeMigration.FileChange Change, byte[]? Before, byte[]? After);
    internal static void Validate(PreparedNativeMigration plan)
    {
        foreach (var change in plan.Changes)
        {
            ReadExpected(OwnedPath(plan.Mirror.OriginalRoot, change.RelativePath), change.BeforeHash);
            ReadExpected(OwnedPath(plan.Mirror.StageRoot, change.RelativePath), change.AfterHash);
        }
    }

    internal static Result Publish(PreparedNativeMigration plan, Func<bool> canonicalGraphsReleased)
    {
        if (!canonicalGraphsReleased()) throw new InvalidOperationException("Release current canonical graphs before publishing a native migration.");
        var mirror = plan.Mirror;
        var files = new List<PreparedFile>();
        var createdDirectories = new List<(string Path, OwnedDirectoryIdentity Identity)>();
        var applied = new List<PreparedFile>();
        string? backupDirectory = null;
        foreach (var change in plan.Changes)
        {
            var original = OwnedPath(mirror.OriginalRoot, change.RelativePath);
            var stage = OwnedPath(mirror.StageRoot, change.RelativePath);
            files.Add(new(change, ReadExpected(original, change.BeforeHash), ReadExpected(stage, change.AfterHash)));
        }
        // Keep exact originals privately even when the user opts out of a durable backup.
        Directory.CreateDirectory(mirror.RecoveryRoot);
        foreach (var file in files.Where(file => file.Before != null))
        {
            var recovery = OwnedPath(mirror.RecoveryRoot, file.Change.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
            WindowsOwnedAssetFileWriter.Write(recovery, null, file.Before!);
        }
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, kind = "native-forward-migration", originalRoot = mirror.OriginalRoot,
            originalOpenPath = mirror.OriginalOpenPath, preparedAt = plan.PreparedAt,
            engineAssembly = typeof(Stride.Core.Assets.PackageSession).Assembly.FullName,
            assetVersions = plan.AssetVersions, decisions = plan.Decisions,
            files = files.Select(file => new { path = file.Change.RelativePath,
                before = file.Change.BeforeHash == null ? null : Convert.ToHexString(file.Change.BeforeHash),
                after = file.Change.AfterHash == null ? null : Convert.ToHexString(file.Change.AfterHash) })
        }, new JsonSerializerOptions { WriteIndented = true });
        WindowsOwnedAssetFileWriter.Write(Path.Combine(mirror.RecoveryRoot, "migration.json"), null, manifest);
        if (plan.BackupOriginals && files.Count > 0)
        {
            backupDirectory = Path.Combine(mirror.OriginalRoot, ".stride-backup-native-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            WindowsOwnedDirectory.Create(backupDirectory);
            foreach (var file in files.Where(file => file.Before != null))
            {
                var destination = OwnedPath(backupDirectory, file.Change.RelativePath);
                EnsureDirectories(backupDirectory, Path.GetDirectoryName(destination)!, null);
                WindowsOwnedAssetFileWriter.Write(destination, null, file.Before!);
            }
            WindowsOwnedAssetFileWriter.Write(Path.Combine(backupDirectory, "migration.json"), null, manifest);
        }
        try
        {
            // No stale clean-load may race the staged publication boundary.
            if (!canonicalGraphsReleased()) throw new InvalidOperationException("Canonical graphs were recreated while preparing migration publication.");
            foreach (var file in files)
            {
                var path = OwnedPath(mirror.OriginalRoot, file.Change.RelativePath);
                if (file.After != null)
                {
                    EnsureDirectories(mirror.OriginalRoot, Path.GetDirectoryName(path)!, createdDirectories);
                    WindowsOwnedAssetFileWriter.Write(path, file.Change.BeforeHash, file.After);
                }
                else
                {
                    ReadExpected(path, file.Change.BeforeHash);
                    WindowsOwnedAssetFileDeletion.Delete(path, file.Change.BeforeHash!);
                }
                applied.Add(file);
            }
            return new(mirror.OriginalOpenPath, backupDirectory, files.Count, DateTimeOffset.UtcNow);
        }
        catch (Exception failure)
        {
            var recoveryFailures = new List<Exception>();
            // A file writer aggregates failures only when its own exact handle restoration
            // failed. Its private originals must outlive disposal even before applied.Add.
            if (failure is AggregateException) mirror.PreserveForRecovery = true;
            foreach (var file in applied.AsEnumerable().Reverse())
            {
                try
                {
                    var path = OwnedPath(mirror.OriginalRoot, file.Change.RelativePath);
                    if (file.Before != null) WindowsOwnedAssetFileWriter.Write(path, file.Change.AfterHash, file.Before);
                    else WindowsOwnedAssetFileDeletion.Delete(path, file.Change.AfterHash!);
                }
                catch (Exception recovery) { recoveryFailures.Add(recovery); }
            }
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
            {
                try { WindowsOwnedDirectory.Remove(directory.Path, directory.Identity); }
                catch (Exception recovery) { recoveryFailures.Add(recovery); }
            }
            if (recoveryFailures.Count > 0)
            {
                mirror.PreserveForRecovery = true;
                throw new AggregateException("Native migration publication and restoration failed. Exact originals are retained at " + mirror.RecoveryRoot,
                    new[] { failure }.Concat(recoveryFailures));
            }
            if (mirror.PreserveForRecovery) throw new AggregateException("Native migration recovery originals are retained at " + mirror.RecoveryRoot, failure);
            throw;
        }
    }
    private static string OwnedPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!NativeMigrationMirror.Inside(root, path) || string.Equals(root, path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid migration file path.");
        for (var current = path; NativeMigrationMirror.Inside(root, current); current = Path.GetDirectoryName(current)!)
        { NativeMigrationMirror.RejectLink(current); if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break; }
        return path;
    }
    private static byte[]? ReadExpected(string path, byte[]? hash)
    {
        if (hash == null)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("A migration destination appeared after preparation: " + path);
            return null;
        }
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var bytes = new MemoryStream(); input.CopyTo(bytes); var content = bytes.ToArray();
        if (!SHA256.HashData(content).AsSpan().SequenceEqual(hash)) throw new IOException("A migration file changed after preparation: " + path);
        return content;
    }
    private static void EnsureDirectories(string root, string path, List<(string Path, OwnedDirectoryIdentity Identity)>? created)
    {
        if (!NativeMigrationMirror.Inside(root, path)) throw new IOException("Migration directory is outside the owned root.");
        NativeMigrationMirror.RejectLink(path);
        if (Directory.Exists(path)) return;
        EnsureDirectories(root, Path.GetDirectoryName(path)!, created);
        var identity = WindowsOwnedDirectory.Create(path); created?.Add((path, identity));
    }
}
