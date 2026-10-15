using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    public Asset AuthoredAsset => Asset;
    public long EditRevision => revision;
    private string[]? savedAssetTags;
    public IReadOnlyList<string> SavedTags => savedAssetTags ??= Asset.Tags.ToArray();
    private void MarkAssetTagsSaved() => savedAssetTags = Asset.Tags.ToArray();
    public void SetTags(IReadOnlyList<string> tags)
    {
        var edit = PrepareTags(tags);
        if (edit == null) return;
        try { edit.Apply(); edit.Commit(); }
        catch { edit.Restore(); throw; }
    }
    public PreparedAssetTagsEdit? PrepareTags(IReadOnlyList<string> tags)
    {
        EndEdits();
        if (!CanSave) throw new InvalidOperationException("This asset can't be edited.");
        var before = Asset.Tags.ToArray(); var after = tags.Distinct(StringComparer.Ordinal).ToArray();
        savedAssetTags ??= before;
        if (before.SequenceEqual(after)) return null;
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        return new PreparedAssetTagsEdit(Asset, before, after, beforeRevision, afterRevision, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = afterRevision; LastAction = "Edit asset tags";
        }, () => revision = beforeRevision, () => revision = afterRevision);
    }
}
