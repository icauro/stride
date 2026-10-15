using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
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
        EndEdit();
        if (!CanSave) throw new InvalidOperationException("This asset can't be edited.");
        var before = Asset.Tags.ToArray(); var after = tags.Distinct(StringComparer.Ordinal).ToArray();
        savedAssetTags ??= before;
        if (before.SequenceEqual(after)) return null;
        if (SessionHistory != null)
        {
            var collection = (Stride.Core.Assets.Quantum.IAssetObjectNode)AuthoringGraph.RootNode[nameof(Asset.Tags)].Target;
            if (!CanEditAssetSettingNode(collection) || collection.Indices.Any(index => AuthoringGraph.IsInheritedNodeSealed(collection, index)))
                throw new InvalidOperationException("These asset tags are sealed.");
            var preparedRevision = revision;
            return new PreparedAssetTagsEdit(Asset, before, after, preparedRevision, () => revision,
                () =>
                {
                    // Earlier members of this atomic selection may have propagated
                    // their native base tags into this same retained derived graph.
                    if (!SessionHistory.IsCapturingOperations && (revision != preparedRevision || !Asset.Tags.SequenceEqual(before)))
                        throw new InvalidOperationException("The asset tags changed after preparation.");
                    if (!CanEditAssetSettingNode(collection) || collection.Indices.Any(index => AuthoringGraph.IsInheritedNodeSealed(collection, index)))
                        throw new InvalidOperationException("These asset tags are sealed.");
                    return ApplyPreparedAssetMutation(() =>
                    {
                        foreach (var index in collection.Indices.Reverse().ToArray())
                            if (collection.Retrieve(index) is string existing && !after.Contains(existing, StringComparer.Ordinal)) collection.Remove(existing, index);
                        for (var ordinal = 0; ordinal < after.Length; ordinal++)
                        {
                            var destination = new Stride.Core.Quantum.NodeIndex(ordinal);
                            var indices = collection.Indices.ToArray();
                            if (ordinal < indices.Length && Equals(collection.Retrieve(destination), after[ordinal])) continue;
                            var existing = indices.FirstOrDefault(index => Equals(collection.Retrieve(index), after[ordinal]), Stride.Core.Quantum.NodeIndex.Empty);
                            if (existing.IsEmpty) collection.Add(after[ordinal], destination);
                            else
                            {
                                if (!collection.TryIndexToId(existing, out var id) || id == default) throw new InvalidOperationException("The authored tag has no stable item identity.");
                                var value = collection.Retrieve(existing);
                                collection.Remove(value, existing); collection.Restore(value, destination, id);
                            }
                        }
                    }, "Edit asset tags");
                });
        }
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        return new PreparedAssetTagsEdit(Asset, before, after, beforeRevision, afterRevision, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = afterRevision; LastAction = "Edit asset tags";
        }, () => revision = beforeRevision, () => revision = afterRevision);
    }
}
