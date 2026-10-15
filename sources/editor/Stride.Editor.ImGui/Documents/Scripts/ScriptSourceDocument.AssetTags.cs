using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    protected long SourceEditRevision => revision;
    protected Guid? SourceNextUndoOperationId => historyScope.Stack.NextUndoTransaction is { Operations.Count: 1 } undo ? undo.Operations[0].Id : null;
    protected Guid? SourceNextRedoOperationId => historyScope.Stack.NextRedoTransaction is { Operations.Count: 1 } redo ? redo.Operations[0].Id : null;
    protected PreparedAssetTagsEdit? PrepareSourceAssetTags(IReadOnlyList<string> tags)
    {
        if (asset == null || !CanSave) throw new InvalidOperationException("This source asset can't be edited.");
        if (SessionHistory != null && !ReferenceEquals(SessionHistory.Registry.Find(Path)?.Document, this))
            throw new InvalidOperationException("The source is no longer its current canonical authored owner.");
        var before = asset.Tags.ToArray(); var after = tags.Distinct(StringComparer.Ordinal).ToArray();
        if (before.SequenceEqual(after)) return null;
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        return new PreparedAssetTagsEdit(asset, before, after, beforeRevision, afterRevision, operation =>
        {
            if (revision != beforeRevision) throw new InvalidOperationException("The source changed before its tag edit was committed.");
            using var transaction = historyScope.CreateTransaction("Edit asset tags");
            historyScope.Record(operation); SetSourceTagRevision(afterRevision);
        }, () => SetSourceTagRevision(beforeRevision), () => SetSourceTagRevision(afterRevision));
    }
    private void SetSourceTagRevision(long target)
    {
        revision = target; Version++;
        if (sourceItem != null) sourceItem.IsDirty = IsDirty;
    }
}
