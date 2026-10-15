using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stride.Editor.ImGui;

/// <summary>Fresh output ownership; compensation never recursively deletes unrecorded namespace entries.</summary>
internal sealed class ProjectTemplatePublication
{
    private sealed record FileReceipt(string RelativePath, byte[] Hash, OwnedFileIdentity Identity);
    private sealed record DirectoryReceipt(string RelativePath, OwnedDirectoryIdentity Identity);
    private readonly List<FileReceipt> files = [];
    private readonly List<DirectoryReceipt> directories = [];
    private readonly List<(string Path, OwnedDirectoryIdentity Identity)> createdParents = [];
    internal string Stage { get; }
    internal string Destination { get; }
    internal OwnedDirectoryIdentity StageIdentity { get; }
    private readonly string marker = ".stride-project-generation";
    private OwnedFileWriteResult? markerOwner;
    private bool projectPublished, committed;
    private string? externalSolution;
    private OwnedFileWriteResult? externalReceipt;
    private bool treeCaptured;

    internal ProjectTemplatePublication(string destination)
    {
        Destination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(Destination)!;
        Stage = Path.Combine(parent, ".stride-project-" + Guid.NewGuid().ToString("N"));
        var stageCreated = false;
        try
        {
            var parentIdentity = EnsureDirectory(parent);
            StageIdentity = WindowsOwnedDirectory.Create(Stage, parentIdentity); stageCreated = true;
            markerOwner = WindowsOwnedAssetFileWriter.WriteNewInDirectory(Path.Combine(Stage, marker), StageIdentity, Encoding.UTF8.GetBytes(Destination));
        }
        catch (Exception failure)
        {
            // The native file writer retains a failed new-object handle until
            // exact recovery; an occupied stage is never forcibly deleted.
            var errors = new List<Exception>();
            if (stageCreated)
                try { WindowsOwnedDirectory.Remove(Stage, StageIdentity); } catch (Exception cleanup) { errors.Add(cleanup); }
            if (errors.Count == 0)
                foreach (var directory in createdParents.AsEnumerable().Reverse())
                    try { WindowsOwnedDirectory.Remove(directory.Path, directory.Identity); } catch (Exception cleanup) { errors.Add(cleanup); }
            if (errors.Count > 0)
                throw new AggregateException("The generation stage could not be restored; its native ownership is retained at " + Stage,
                    new[] { failure }.Concat(errors));
            throw;
        }
    }

    internal OwnedDirectoryIdentity EnsureDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path)) return WindowsOwnedDirectory.Capture(path);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? throw new DirectoryNotFoundException(path);
        var parentIdentity = EnsureDirectory(parent);
        var identity = WindowsOwnedDirectory.Create(path, parentIdentity);
        if (!Inside(Stage, path)) createdParents.Add((path, identity));
        return identity;
    }

    internal static bool Inside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
    private string Root => projectPublished ? Destination : Stage;

    private void CaptureTree()
    {
        if (treeCaptured) return;
        files.Clear(); directories.Clear();
        void Visit(string path)
        {
            if (!Inside(Stage, path)) throw new IOException("The generated output escaped its owned stage.");
            var identity = WindowsOwnedDirectory.Capture(path);
            directories.Add(new(Path.GetRelativePath(Stage, path), identity));
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The generated output contains a redirected entry: " + entry);
                if ((attributes & FileAttributes.Directory) != 0) Visit(entry);
                else
                {
                    using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var fileIdentity = WindowsOwnedAssetFileWriter.CaptureIdentity(stream);
                    files.Add(new(Path.GetRelativePath(Stage, entry), SHA256.HashData(stream), fileIdentity));
                }
            }
        }
        if (WindowsOwnedDirectory.Capture(Stage) != StageIdentity) throw new IOException("The generation stage was replaced.");
        Visit(Stage);
        var actualMarker = files.SingleOrDefault(file => file.RelativePath == marker);
        if (markerOwner is not { } owner || actualMarker == null || actualMarker.Identity != owner.Identity || !actualMarker.Hash.AsSpan().SequenceEqual(owner.ContentHash))
            throw new IOException("The authenticated generation marker changed.");
        treeCaptured = true;
    }

    internal string Publish(string solutionPath, byte[]? externalSolutionBytes, CancellationToken cancellation)
    {
        var outputParent = WindowsOwnedDirectory.Capture(Path.GetDirectoryName(Destination)!);
        var parentIdentity = externalSolutionBytes == null ? (OwnedDirectoryIdentity?)null : EnsureDirectory(Path.GetDirectoryName(solutionPath)!);
        CaptureTree(); cancellation.ThrowIfCancellationRequested();
        using var directory = new WindowsOwnedDirectoryRename(Stage, StageIdentity);
        directory.Move(Destination, outputParent); projectPublished = true;
        if (externalSolutionBytes != null)
        {
            cancellation.ThrowIfCancellationRequested();
            externalSolution = solutionPath;
            externalReceipt = WindowsOwnedAssetFileWriter.WriteNewInDirectory(solutionPath, parentIdentity!.Value, externalSolutionBytes);
        }
        cancellation.ThrowIfCancellationRequested();
        var markerReceipt = files.Single(file => file.RelativePath == marker);
        WindowsOwnedAssetFileDeletion.Delete(Path.Combine(Destination, marker), markerReceipt.Hash, true, markerReceipt.Identity);
        committed = true;
        return solutionPath;
    }

    internal void Compensate(Exception failure)
    {
        if (committed) return;
        var errors = new List<Exception>();
        if (externalReceipt is { } receipt && externalSolution != null)
        {
            try { WindowsOwnedAssetFileDeletion.Delete(externalSolution, receipt.ContentHash, true, receipt.Identity); externalReceipt = null; }
            catch (Exception cleanup) { errors.Add(cleanup); }
        }
        try
        {
            if (!projectPublished) CaptureTree();
            if (WindowsOwnedDirectory.Capture(Root) != StageIdentity) throw new IOException("The owned generation directory was replaced.");
            // Files are deleted only through their physical identity AND bytes.
            // New/modified foreign entries survive and prevent directory removal.
            foreach (var file in files.Where(file => file.RelativePath != marker))
                try { WindowsOwnedAssetFileDeletion.Delete(Path.Combine(Root, file.RelativePath), file.Hash, false, file.Identity); }
                catch (Exception cleanup) { errors.Add(cleanup); }
            foreach (var child in directories.AsEnumerable().Reverse().Where(directory => directory.RelativePath != "."))
                try { WindowsOwnedDirectory.Remove(Path.Combine(Root, child.RelativePath), child.Identity); }
                catch (Exception cleanup) { errors.Add(cleanup); }
            if (errors.Count == 0)
            {
                var markerReceipt = files.Single(file => file.RelativePath == marker);
                WindowsOwnedAssetFileDeletion.Delete(Path.Combine(Root, marker), markerReceipt.Hash, false, markerReceipt.Identity);
                WindowsOwnedDirectory.Remove(Root, StageIdentity);
            }
        }
        catch (Exception cleanup) { errors.Add(cleanup); }
        if (errors.Count == 0)
            foreach (var parent in createdParents.AsEnumerable().Reverse())
                try { WindowsOwnedDirectory.Remove(parent.Path, parent.Identity); }
                catch (Exception cleanup) { errors.Add(cleanup); }
        if (errors.Count == 0) return;
        string? recovery = null;
        try
        {
            var parent = Path.GetDirectoryName(Destination)!;
            recovery = Path.Combine(parent, ".stride-project-recovery-" + Guid.NewGuid().ToString("N") + ".json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, stage = Stage, output = Root, destination = Destination,
                directory = StageIdentity, files, directories, externalSolution,
                external = externalReceipt is { } owner ? new { hash = owner.ContentHash, identity = owner.Identity } : null,
                marker = markerOwner is { } markerReceipt ? new { hash = markerReceipt.ContentHash, identity = markerReceipt.Identity } : null,
                createdParents = createdParents.Select(parent => new { path = parent.Path, identity = parent.Identity }).ToArray(),
                failures = errors.Select(error => error.ToString()).ToArray() });
            WindowsOwnedAssetFileWriter.WriteNewInDirectory(recovery, WindowsOwnedDirectory.Capture(parent), bytes);
        }
        catch (Exception journalFailure) { errors.Add(journalFailure); }
        throw new AggregateException("Project generation could not restore all owned outputs. Retained output: " + Root
            + (recovery == null ? "" : "; recovery record: " + recovery), new[] { failure }.Concat(errors));
    }
}
