using System.Security.Cryptography;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Compensated ownership of precisely the resource bytes approved by the user.</summary>
internal sealed class ResourceSourceCopyOperation(PreparedResourceSourceCopy[] files, string packageRoot, Action validate, string[]? resourceRoots = null) : Operation
{
    private readonly Dictionary<string, OwnedSourceDirectoriesOperation> directories = new(StringComparer.OrdinalIgnoreCase);
    private OwnedSourceDirectoriesOperation DirectoryOwner(string destination)
    {
        var roots = resourceRoots ?? [packageRoot];
        if (!roots.Any(root => destination.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The resource destination is outside the package resource folders.");
        var volume = Path.GetPathRoot(destination)!;
        if (!directories.TryGetValue(volume, out var owner)) directories.Add(volume, owner = new(volume));
        return owner;
    }
    private void RemoveDirectories()
    {
        foreach (var owner in directories.Values) owner.ValidateOwnedDirectories();
        var removed = new List<(OwnedSourceDirectoriesOperation Owner, bool[] Phases)>();
        try
        {
            foreach (var owner in directories.Values.Reverse()) if (owner.HasDirectories)
            {
                var phases = owner.CaptureDirectoryPhases();
                owner.UndoOwnedDirectories();
                removed.Add((owner, phases));
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var receipt in removed.AsEnumerable().Reverse()) try { receipt.Owner.RestoreDirectoryPhases(receipt.Phases); } catch (Exception error) { errors.Add(error); }
            if (errors.Count > 1) throw new AggregateException("Resource directory compensation failed.", errors);
            throw;
        }
    }
    private void RestoreDirectories()
    {
        foreach (var owner in directories.Values) owner.ValidateOwnedDirectories();
        var restored = new List<(OwnedSourceDirectoriesOperation Owner, bool[] Phases)>();
        try
        {
            foreach (var owner in directories.Values) if (owner.HasDirectories)
            {
                var phases = owner.CaptureDirectoryPhases();
                owner.RedoOwnedDirectories();
                restored.Add((owner, phases));
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var receipt in restored.AsEnumerable().Reverse()) try { receipt.Owner.RestoreDirectoryPhases(receipt.Phases); } catch (Exception error) { errors.Add(error); }
            if (errors.Count > 1) throw new AggregateException("Resource directory restoration failed.", errors);
            throw;
        }
    }
    private bool applied, directoriesRecorded, failedDirectoryCompensation;
    // A receipt changes only after the actual native CAS operation returns. A failed
    // undo/compensation leaves its real mixed phase available to the same operation.
    private enum FilePhase { Untouched, Copied, Original }
    private readonly FilePhase[] filePhases = new FilePhase[files.Length];
    private readonly SourceReadLeases sourceReads = new(files.Select(file => file.SourceRead).Distinct().ToList());
    public override bool HasEffect => files.Length != 0;
    internal void ValidateApplied()
    {
        validate();
        if (!applied || filePhases.Any(phase => phase != FilePhase.Copied))
            throw new InvalidOperationException("The resource copies have unfinished compensation.");
        if (directories.Values.Any(owner => owner.HasAbsentDirectories))
            throw new InvalidOperationException("The resource directories have unfinished compensation.");
        foreach (var owner in directories.Values) owner.ValidateOwnedDirectories();
        ValidateRecordedFiles();
    }
    private void ValidateRecordedFiles()
    {
        for (var index = 0; index < files.Length; index++)
        {
            var phase = filePhases[index];
            if (phase == FilePhase.Untouched) continue;
            var file = files[index];
            var expected = phase == FilePhase.Copied ? file.Hash : file.PreviousHash;
            if (expected == null)
            {
                if (File.Exists(file.Destination) || Directory.Exists(file.Destination))
                    throw new IOException("The restored resource path changed outside the editor.");
            }
            else
            {
                using var source = new FileStream(file.Destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!SHA256.HashData(source).AsSpan().SequenceEqual(expected))
                    throw new IOException("The copied resource changed outside the editor.");
            }
        }
    }
    private void WriteCopied(int index)
    {
        var file = files[index];
        WindowsOwnedAssetFileWriter.Write(file.Destination, file.PreviousHash, file.Contents);
        filePhases[index] = FilePhase.Copied;
    }
    private void WriteOriginal(int index)
    {
        RestorePrevious(files[index]);
        filePhases[index] = FilePhase.Original;
    }
    internal void ApplyInitial()
    {
        // Both source and destination were snapshotted off the actor. Reject a changed
        // source before any write; destinations are checked atomically by the writer.
        try
        {
            validate();
            if (applied) throw new InvalidOperationException("The resource copies are already published.");
            foreach (var file in files)
            {
                if (OperatingSystem.IsWindows()) WindowsSourceFileIdentity.Validate(file.Source, WindowsSourceFileIdentity.Capture(file.SourceRead));
                else
                {
                    file.SourceRead.Position = 0;
                    if (!SHA256.HashData(file.SourceRead).AsSpan().SequenceEqual(file.Hash)) throw new IOException("The source file changed during import: " + file.Source);
                }
            }
            Apply(false);
        }
        finally { sourceReads.Dispose(); }
    }
    internal void CancelInitial()
    {
        if (applied || filePhases.Any(phase => phase == FilePhase.Copied)) Remove(false);
        else ValidateRecordedFiles();
        if (failedDirectoryCompensation || directories.Values.Any(owner => owner.HasPresentDirectories))
        {
            RemoveDirectories();
            failedDirectoryCompensation = false;
        }
        sourceReads.Dispose();
    }
    private void Apply(bool replay)
    {
        validate();
        if (applied || filePhases.Any(phase => phase == FilePhase.Copied) || failedDirectoryCompensation)
            throw new InvalidOperationException("The resource copies have unfinished compensation.");
        ValidateRecordedFiles();
        var written = new List<int>(); var restoredDirectories = false;
        try
        {
            if (replay && directoriesRecorded) { RestoreDirectories(); restoredDirectories = true; }
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                if (!replay) DirectoryOwner(file.Destination).Ensure(Path.GetDirectoryName(file.Destination)!);
                WriteCopied(index);
                written.Add(index);
            }
            directoriesRecorded = directories.Values.Any(owner => owner.HasDirectories); applied = true;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var index in written.AsEnumerable().Reverse())
                try { WriteOriginal(index); }
                catch (Exception error) { errors.Add(error); }
            try { if (!replay || restoredDirectories || directories.Values.Any(owner => owner.HasPresentDirectories)) RemoveDirectories(); }
            catch (Exception error) { failedDirectoryCompensation = true; errors.Add(error); }
            if (errors.Count > 1) throw new AggregateException("Resource copying and compensation failed.", errors);
            throw;
        }
    }
    private static void RestorePrevious(PreparedResourceSourceCopy file)
    {
        if (!File.Exists(file.Destination)) throw new IOException("The copied resource changed outside the editor.");
        if (file.Previous == null) WindowsOwnedAssetFileDeletion.Delete(file.Destination, file.Hash, requireExists: true);
        else WindowsOwnedAssetFileWriter.Write(file.Destination, file.Hash, file.Previous);
    }
    private void Remove(bool validateOwner = true)
    {
        if (validateOwner) validate();
        ValidateRecordedFiles();
        var restored = new List<int>();
        try
        {
            for (var index = files.Length - 1; index >= 0; index--)
            {
                if (filePhases[index] != FilePhase.Copied) continue;
                WriteOriginal(index);
                restored.Add(index);
            }
            if (directoriesRecorded) RemoveDirectories(); applied = false;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var index in restored.AsEnumerable().Reverse())
                try { WriteCopied(index); }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count > 1) throw new AggregateException("Resource copy undo and compensation failed.", errors);
            throw;
        }
    }
    protected override void Rollback() => Remove();
    protected override void Rollforward() => Apply(true);
}
