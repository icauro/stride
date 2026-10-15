namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    internal void TransferProjectItemFrom(ProjectBuildSettingsDocument source, string itemType, string beforeInclude, string afterInclude)
    {
        if (ReferenceEquals(source, this)) { RelocateProjectItem(itemType, beforeInclude, afterInclude); return; }
        if (!CanSave || !source.CanSave || !ReferenceEquals(source.SessionHistory, SessionHistory)) throw new InvalidOperationException("Both projects require the same editable authored session.");
        var before = properties.Snapshot; var sourceBefore = source.properties.Snapshot;
        var beforeRevision = revision; var sourceBeforeRevision = source.revision;
        try
        {
            properties.TransferProjectItemFrom(source.properties, itemType, beforeInclude, afterInclude);
            var sourceAfter = source.properties.Snapshot; var after = properties.Snapshot;
            if (sourceBefore != sourceAfter)
            { source.revision = ++source.nextRevision; source.history.Record(new BuildOperation(source, sourceBefore, sourceAfter, sourceBeforeRevision, source.revision)); }
            if (before != after)
            { revision = ++nextRevision; history.Record(new BuildOperation(this, before, after, beforeRevision, revision)); }
        }
        catch { source.Apply(sourceBefore, sourceBeforeRevision); Apply(before, beforeRevision); throw; }
    }
}
