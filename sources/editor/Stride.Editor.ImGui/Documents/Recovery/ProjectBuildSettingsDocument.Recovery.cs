using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    internal EditorRecoveryRecord CaptureRecovery()
        => new(Path, "project", null, null, project.Package.FullPath.ToOSPath(), null, savedDiskHash.ToArray(), properties.Serialize(), [], null);
    internal void RestoreRecovery(EditorRecoveryRecord record)
    {
        if (!CanSave || IsDirty || !string.Equals(Path, record.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The current project settings cannot be replaced by recovery.");
        using var restored = new Stride.Core.Assets.AuthoredProjectProperties(record.Payload, Path);
        var before = properties.Snapshot; var after = restored.Snapshot;
        var beforeRevision = revision; var afterRevision = ++nextRevision; var previousBaseline = savedDiskHash.ToArray();
        var operation = new RecoveryProjectOperation(this, before, after, beforeRevision, afterRevision, previousBaseline, record.BaselineHash.ToArray());
        using var transaction = history.CreateTransaction("Recover project settings");
        try { Apply(after, afterRevision); savedDiskHash = record.BaselineHash.ToArray(); history.Record(operation); }
        catch { Apply(before, beforeRevision); savedDiskHash = previousBaseline; throw; }
    }
    private sealed class RecoveryProjectOperation(ProjectBuildSettingsDocument owner, string before, string after,
        long beforeRevision, long afterRevision, byte[] beforeHash, byte[] afterHash) : Operation
    {
        protected override void Rollback() { owner.Apply(before, beforeRevision); owner.savedDiskHash = beforeHash.ToArray(); }
        protected override void Rollforward() { owner.Apply(after, afterRevision); owner.savedDiskHash = afterHash.ToArray(); }
    }
}
