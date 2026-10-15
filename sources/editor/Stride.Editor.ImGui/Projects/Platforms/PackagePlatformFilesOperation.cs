using System.Security.Cryptography;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>One native file/directory journal for an actual prepared platform update.</summary>
internal sealed class PackagePlatformFilesOperation : Operation
{
    private sealed class FilePhase(PreparedPackagePlatformUpdate.FileChange change)
    {
        internal readonly PreparedPackagePlatformUpdate.FileChange Change = change;
        internal bool Forward;
        internal OwnedFileIdentity? Identity = change.BeforeIdentity;
    }
    private readonly string root;
    private readonly FilePhase[] files;
    private readonly string[] afterDirectories;
    private readonly OwnedDirectoryPhaseReceipts removedDirectories;
    private readonly OwnedSourceDirectoriesOperation createdDirectories;
    private bool applied;
    private bool? recoveryGoal;
    internal PackagePlatformFilesOperation(PreparedPackagePlatformUpdate prepared)
    {
        root = prepared.Mirror.OriginalRoot;
        files = prepared.Changes.Select(change => new FilePhase(change)).ToArray();
        afterDirectories = prepared.AfterDirectories;
        removedDirectories = new(root); createdDirectories = new(root);
        foreach (var directory in prepared.BeforeDirectories.Where(directory => !afterDirectories.Contains(directory.RelativePath, StringComparer.OrdinalIgnoreCase))
            .OrderBy(directory => directory.RelativePath.Count(character => character is '/' or '\\')))
            removedDirectories.RetainExisting(PathFor(directory.RelativePath), directory.Identity);
    }
    private string PathFor(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!NativeMigrationMirror.Inside(root, path) || SamePath(root, path)) throw new IOException("The platform output is outside its captured root.");
        for (var current = path; NativeMigrationMirror.Inside(root, current); current = Path.GetDirectoryName(current)!)
        { NativeMigrationMirror.RejectLink(current); if (SamePath(root, current)) break; }
        return path;
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    internal void Change(bool forward)
    {
        if (recoveryGoal is { } goal) { Apply(goal); applied = goal; recoveryGoal = null; }
        if (applied == forward) return;
        try { Apply(forward); applied = forward; }
        catch (Exception failure)
        {
            try { Apply(!forward); }
            catch (Exception restoration)
            {
                recoveryGoal = !forward;
                throw new AggregateException("Platform file publication and restoration failed.", failure, restoration);
            }
            throw;
        }
    }
    internal bool HasPublishedFileOwnership(string path, byte[]? hash)
    {
        if (!applied || recoveryGoal != null || !NativeMigrationMirror.Inside(root, path)) return false;
        createdDirectories.ValidateOwnedDirectories();
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        var phase = files.SingleOrDefault(file => string.Equals(file.Change.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
        if (hash == null)
            return (phase == null || phase.Forward && phase.Change.After == null) && !File.Exists(path) && !Directory.Exists(path);
        if (phase is not { Forward: true, Identity: not null } || phase.Change.After == null
            || !SHA256.HashData(phase.Change.After).AsSpan().SequenceEqual(hash)) return false;
        ValidateFile(phase); return true;
    }
    private void ValidateFile(FilePhase file)
    {
        var path = PathFor(file.Change.RelativePath); var expected = file.Forward ? file.Change.After : file.Change.Before;
        if (expected == null)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("A platform file appeared outside the editor: " + path);
            return;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Identity == null || WindowsOwnedAssetFileWriter.CaptureIdentity(stream) != file.Identity.Value
            || !SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(expected)))
            throw new IOException("A platform file changed outside the editor: " + path);
    }
    private void Apply(bool forward)
    {
        // Every successful native phase remains explicit. Internal writer recovery
        // retains its own actual handle; no uncertain path is reopened as success.
        foreach (var file in files) ValidateFile(file);
        if (forward)
        {
            createdDirectories.RedoOwnedDirectories();
            foreach (var relative in afterDirectories.OrderBy(path => path.Count(character => character is '/' or '\\')))
                createdDirectories.Ensure(PathFor(relative));
        }
        else removedDirectories.RedoOwnedDirectories();
        foreach (var file in forward ? files : files.Reverse())
        {
            if (file.Forward == forward) continue;
            var path = PathFor(file.Change.RelativePath);
            var previous = file.Forward ? file.Change.After : file.Change.Before;
            var next = forward ? file.Change.After : file.Change.Before;
            if (previous != null && next != null && previous.AsSpan().SequenceEqual(next))
            { file.Forward = forward; continue; }
            if (next == null)
            {
                WindowsOwnedAssetFileDeletion.Delete(path, SHA256.HashData(previous!), true, file.Identity);
                file.Identity = null;
            }
            else
            {
                OwnedFileWriteResult receipt;
                if (previous == null)
                {
                    if (forward) createdDirectories.Ensure(Path.GetDirectoryName(path)!);
                    var parent = WindowsOwnedDirectory.Capture(Path.GetDirectoryName(path)!);
                    receipt = WindowsOwnedAssetFileWriter.WriteNewInDirectory(path, parent, next);
                }
                else receipt = WindowsOwnedAssetFileWriter.Write(path, SHA256.HashData(previous), next, file.Identity);
                file.Identity = receipt.Identity;
            }
            file.Forward = forward;
        }
        if (forward) removedDirectories.UndoOwnedDirectories();
        else createdDirectories.UndoOwnedDirectories();
    }
    protected override void Rollback() => Change(false);
    protected override void Rollforward() => Change(true);
}
