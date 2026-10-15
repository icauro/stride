using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stride.Editor.ImGui;

/// <summary>Durable immutable payloads with an atomically published session manifest; never writes project files.</summary>
internal sealed class EditorRecoveryStore
{
    private const int FormatVersion = 1;
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly string projectPath;
    private readonly string projectDirectory;
    private readonly string sessionDirectory;
    private readonly DateTime processStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private EditorRecoveryManifest? latest;
    private string? latestCheckpointFile, latestCheckpointHash;
    internal const string CompletionFileName = "completion.json";
    public EditorRecoveryStore(string projectPath, string? root = null)
    {
        this.projectPath = System.IO.Path.GetFullPath(projectPath);
        root ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "Recovery");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.projectPath.ToUpperInvariant())));
        projectDirectory = System.IO.Path.Combine(root, key);
        sessionDirectory = System.IO.Path.Combine(projectDirectory, sessionId.ToString("N"));
    }
    public async Task CheckpointAsync(IReadOnlyList<EditorRecoveryRecord> records, string[] loadedAssemblies,
        EditorRecoveryWorkspaceState? workspace = null)
    {
        // Detach presentation arrays on the caller before yielding to the journal writer.
        workspace = workspace?.Snapshot(projectPath);
        await writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (latest?.Completed == true) throw new InvalidOperationException("A completed recovery session cannot accept new checkpoints.");
            Directory.CreateDirectory(sessionDirectory);
            var entries = new List<EditorRecoveryEntry>();
            foreach (var record in records)
            {
                var hash = Convert.ToHexString(SHA256.HashData(record.Payload));
                var path = System.IO.Path.Combine(sessionDirectory, hash + ".snapshot");
                if (!File.Exists(path)) await WriteAtomicAsync(path, record.Payload).ConfigureAwait(false);
                else if (!SHA256.HashData(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).SequenceEqual(SHA256.HashData(record.Payload)))
                    throw new InvalidDataException("An existing recovery payload has failed its integrity check.");
                entries.Add(new(record.Path, record.Kind, record.AssetId, record.Namespace, record.PackagePath, record.AssetLocation,
                    record.BaselineHash.ToArray(), hash, record.Selection.ToArray(), record.PrimarySelection, record.ProjectPath, record.SourceFolders?.ToArray(), record.SourceFolder));
            }
            var manifest = new EditorRecoveryManifest(FormatVersion, sessionId, projectPath, Environment.ProcessId, processStarted,
                DateTime.UtcNow, false, entries.ToArray(), loadedAssemblies.ToArray(), workspace);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            // Retain each committed checkpoint so a damaged latest pointer does not lose prior recovery data.
            var checkpointFile = "checkpoint-" + DateTime.UtcNow.Ticks + ".json";
            await WriteAtomicAsync(System.IO.Path.Combine(sessionDirectory, checkpointFile), bytes).ConfigureAwait(false);
            await WriteAtomicAsync(System.IO.Path.Combine(sessionDirectory, "session.json"), bytes).ConfigureAwait(false);
            latest = manifest;
            latestCheckpointFile = checkpointFile; latestCheckpointHash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { writeGate.Release(); }
    }
    public IReadOnlyList<EditorRecoveryCandidate> Discover()
    {
        if (!Directory.Exists(projectDirectory)) return [];
        var result = new List<EditorRecoveryCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(projectDirectory))
        {
            if (string.Equals(directory, sessionDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            if (HasCompletionProvenance(directory)) continue;
            var path = System.IO.Path.Combine(directory, "session.json");
            EditorRecoveryManifest? manifest = null;
            foreach (var candidate in new[] { path }.Concat(Directory.EnumerateFiles(directory, "checkpoint-*.json").OrderDescending()))
            {
                try
                {
                    manifest = JsonSerializer.Deserialize<EditorRecoveryManifest>(File.ReadAllBytes(candidate));
                    if (manifest?.Workspace != null) manifest = manifest with { Workspace = manifest.Workspace.Snapshot(projectPath) };
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
                { manifest = null; continue; }
                if (manifest is { Version: FormatVersion } && string.Equals(manifest.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)) { path = candidate; break; }
                manifest = null;
            }
            if (manifest is not { Completed: false } || (manifest.Documents.Length == 0 && manifest.Workspace == null) || IsOwnerAlive(manifest)) continue;
            result.Add(new(path, manifest));
        }
        return result.OrderByDescending(candidate => candidate.Manifest.CapturedUtc).ToArray();
    }
    public EditorRecoveryRecord Read(EditorRecoveryCandidate candidate, EditorRecoveryEntry entry)
    {
        if (!candidate.Manifest.Documents.Contains(entry) || candidate.Manifest.Version != FormatVersion
            || !string.Equals(candidate.Manifest.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The recovery entry has another project owner.");
        if (entry.PayloadHash.Length != 64 || !entry.PayloadHash.All(Uri.IsHexDigit)) throw new InvalidDataException("The recovery payload identity is invalid.");
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(candidate.ManifestPath))!;
        if (!System.IO.Path.GetFullPath(directory).StartsWith(System.IO.Path.GetFullPath(projectDirectory) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recovery payload is outside its project journal.");
        var payload = File.ReadAllBytes(System.IO.Path.Combine(directory, entry.PayloadHash + ".snapshot"));
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(payload)), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recovery payload has failed its integrity check.");
        return new(entry.Path, entry.Kind, entry.AssetId, entry.Namespace, entry.PackagePath, entry.AssetLocation,
            entry.BaselineHash.ToArray(), payload, entry.Selection.ToArray(), entry.PrimarySelection, entry.ProjectPath, entry.SourceFolders?.ToArray(), entry.SourceFolder);
    }
    public static bool DiskMatches(EditorRecoveryEntry entry)
    {
        if (entry.BaselineHash.Length == 0) return !File.Exists(entry.Path) && !Directory.Exists(entry.Path);
        try { return File.Exists(entry.Path) && SHA256.HashData(File.ReadAllBytes(entry.Path)).SequenceEqual(entry.BaselineHash); }
        catch (IOException) { return false; }
    }
    public async Task CompleteAsync()
    {
        await writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (latest == null) return;
            if (latest.Completed) return;
            if (latestCheckpointFile == null || latestCheckpointHash == null)
                throw new InvalidDataException("Recovery completion has no durable checkpoint provenance.");
            var checkpoint = await File.ReadAllBytesAsync(System.IO.Path.Combine(sessionDirectory, latestCheckpointFile)).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(checkpoint)), latestCheckpointHash, StringComparison.Ordinal))
                throw new InvalidDataException("Recovery completion checkpoint has changed.");
            var completion = new EditorRecoveryCompletion(FormatVersion, sessionId, projectPath, Environment.ProcessId,
                processStarted, latestCheckpointFile, latestCheckpointHash, DateTime.UtcNow);
            // This durable certificate is authoritative even if updating the
            // mutable latest pointer fails, or that pointer is damaged later.
            await WriteAtomicAsync(System.IO.Path.Combine(sessionDirectory, CompletionFileName),
                JsonSerializer.SerializeToUtf8Bytes(completion)).ConfigureAwait(false);
            latest = latest with { Completed = true };
            await WriteAtomicAsync(System.IO.Path.Combine(sessionDirectory, "session.json"), JsonSerializer.SerializeToUtf8Bytes(latest)).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
    private bool HasCompletionProvenance(string directory)
    {
        var path = System.IO.Path.Combine(directory, CompletionFileName);
        if (!File.Exists(path) && !Directory.Exists(path)) return false; // Older active journals remain recoverable.
        try
        {
            var completion = JsonSerializer.Deserialize<EditorRecoveryCompletion>(ReadBounded(path))
                ?? throw new InvalidDataException("Recovery completion certificate is empty.");
            var leaf = completion.CheckpointFile;
            if (completion.Version != FormatVersion || completion.SessionId == Guid.Empty || completion.ProcessId <= 0
                || !string.Equals(System.IO.Path.GetFileName(directory), completion.SessionId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(completion.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase)
                || completion.ProcessStartedUtc == default || completion.CompletedUtc == default
                || string.IsNullOrEmpty(leaf) || !leaf.StartsWith("checkpoint-", StringComparison.Ordinal) || !leaf.EndsWith(".json", StringComparison.Ordinal)
                || leaf.Length <= 16 || !leaf.AsSpan(11, leaf.Length - 16).ToArray().All(character => character is >= '0' and <= '9')
                || string.IsNullOrEmpty(completion.CheckpointHash) || completion.CheckpointHash.Length != 64 || !completion.CheckpointHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Recovery completion certificate has inconsistent ownership.");
            var bytes = ReadBounded(System.IO.Path.Combine(directory, leaf));
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), completion.CheckpointHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery completion checkpoint integrity failed.");
            var manifest = JsonSerializer.Deserialize<EditorRecoveryManifest>(bytes);
            if (manifest is not { Version: FormatVersion, Completed: false }
                || manifest.SessionId != completion.SessionId || manifest.ProcessId != completion.ProcessId
                || manifest.ProcessStartedUtc != completion.ProcessStartedUtc
                || !string.Equals(manifest.ProjectPath, completion.ProjectPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery completion checkpoint does not identify its actual session.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A damaged certificate is not permission to revive an older dirty
            // checkpoint. Isolate this session rather than hiding valid siblings.
            Console.Error.WriteLine("Recovery completion provenance rejected: " + error.Message);
        }
        return true;
    }
    private static byte[] ReadBounded(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Recovery metadata exceeds the supported size.");
        return File.ReadAllBytes(path);
    }
    private static bool IsOwnerAlive(EditorRecoveryManifest manifest)
    {
        try { using var process = Process.GetProcessById(manifest.ProcessId); return !process.HasExited && process.StartTime.ToUniversalTime() == manifest.ProcessStartedUtc; }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
    private static async Task WriteAtomicAsync(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough | FileOptions.Asynchronous))
            { await file.WriteAsync(bytes).ConfigureAwait(false); await file.FlushAsync().ConfigureAwait(false); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
