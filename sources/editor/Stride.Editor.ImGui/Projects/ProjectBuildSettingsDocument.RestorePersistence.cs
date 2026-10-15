using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    internal sealed record PersistedProjectFile(byte[] Bytes, byte[] Hash, long SavedRevision);
    private PersistedProjectFile CapturePersistedProjectFile()
    {
        ValidateProjectFileBaseline();
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        var contents = bytes.ToArray();
        if (!SHA256.HashData(contents).AsSpan().SequenceEqual(savedDiskHash))
            throw new InvalidOperationException("The project file changed outside the editor.");
        return new(contents, savedDiskHash.ToArray(), savedRevision);
    }
    private void RestorePersistedProjectFile(PersistedProjectFile expected, PersistedProjectFile target)
    {
        history.EnsureMaySave();
        if (savedRevision != expected.SavedRevision || !savedDiskHash.AsSpan().SequenceEqual(expected.Hash))
            throw new InvalidOperationException("The project's saved boundary changed outside this dependency operation.");
        byte[]? writtenHash = null;
        try
        {
            var restored = WindowsOwnedAssetFileWriter.Write(Path, expected.Hash, target.Bytes);
            writtenHash = restored.ContentHash;
            savedDiskHash = restored.ContentHash; savedRevision = target.SavedRevision;
            lastModified = restored.ModifiedTicks; lastLength = restored.Length;
            history.Saved(savedDiskHash);
        }
        catch (Exception failure)
        {
            if (writtenHash != null)
            {
                try
                {
                    var restored = WindowsOwnedAssetFileWriter.Write(Path, writtenHash, expected.Bytes);
                    savedDiskHash = restored.ContentHash; savedRevision = expected.SavedRevision;
                    lastModified = restored.ModifiedTicks; lastLength = restored.Length;
                    history.Saved(savedDiskHash);
                }
                catch (Exception recovery) { throw new AggregateException("The saved dependency boundary could not be restored.", failure, recovery); }
            }
            throw;
        }
    }
    private sealed class DependencyProjectSaveOperation(ProjectBuildSettingsDocument owner, PersistedProjectFile before,
        PersistedProjectFile after, long authoringRevision, string workingXml, DependencyRange[] workingDependencies) : Operation
    {
        public override bool HasEffect => before.SavedRevision != after.SavedRevision || !before.Hash.AsSpan().SequenceEqual(after.Hash);
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private void Replay(bool forward)
        {
            if (owner.revision != authoringRevision || owner.properties.Snapshot != workingXml
                || !DependenciesMatch(owner.project.DirectDependencies, workingDependencies))
                throw new InvalidOperationException("The authored project changed before saved dependency replay.");
            owner.RestorePersistedProjectFile(forward ? before : after, forward ? after : before);
        }
    }
    public ProjectRestorePersistence BeginDependencyRestorePersistence() => new(this, CapturePersistedProjectFile());
    public sealed class ProjectRestorePersistence : IDisposable
    {
        private readonly ProjectBuildSettingsDocument owner;
        private PersistedProjectFile? original;
        internal ProjectRestorePersistence(ProjectBuildSettingsDocument owner, PersistedProjectFile original)
        { this.owner = owner; this.original = original; }
        public void Save()
        {
            ObjectDisposedException.ThrowIf(original == null, this);
            var before = owner.CapturePersistedProjectFile();
            var workingXml = owner.properties.Snapshot;
            var workingDependencies = CloneDependencies(owner.project.DirectDependencies);
            var savedBytes = owner.IsDirty ? owner.SerializeWorkingProject() : before.Bytes;
            // Restore consumes full authored XML; journal the owned save itself
            // so group undo restores earlier pending edits as dirty again.
            owner.Save();
            var after = new PersistedProjectFile(savedBytes, owner.savedDiskHash.ToArray(), owner.savedRevision);
            var operation = new DependencyProjectSaveOperation(owner, before, after, owner.revision, workingXml, workingDependencies);
            try
            {
                using var transaction = owner.history.CreateTransaction("Save project dependencies");
                owner.history.Record(operation);
            }
            catch (Exception failure)
            {
                try { owner.RestorePersistedProjectFile(after, before); }
                catch (Exception recovery)
                {
                    if (owner.history.AuthoringState is { } state) owner.SessionHistory!.RetainFailedInitialOperation(operation, state);
                    throw new AggregateException("The dependency project save could not be recorded or restored.", failure, recovery);
                }
                throw;
            }
        }
        public void RestoreOriginalFile()
        {
            ObjectDisposedException.ThrowIf(original == null, this);
            owner.RestorePersistedProjectFile(owner.CapturePersistedProjectFile(), original);
        }
        public void Dispose() => original = null;
    }
}
