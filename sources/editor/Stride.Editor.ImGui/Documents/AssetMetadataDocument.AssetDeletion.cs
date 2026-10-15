using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument : IAssetDeletionDocument
{
    public PreparedAssetDeletionReferences? PrepareDeletedReferences(IReadOnlySet<AssetId> deleted)
    {
        CommitEdits();
        if (SessionHistory != null)
        {
            if (!CanSave) throw new InvalidOperationException("These asset references can't be edited.");
            var native = new PreparedAssetDeletionReferences(AuthoringGraph, deleted, revision, () => revision,
                (node, index) => node is Stride.Core.Quantum.IMemberNode member
                    ? CanEditAssetSettingNode(node) && CanAssignAuthoringNode(member)
                    : node is Stride.Core.Assets.Quantum.IAssetObjectNode collection && CanEditAssetSettingItem(collection, index),
                mutate => ApplyInheritedAssetMutation(mutate, "Resolve deleted asset references"));
            return native.HasChanges ? native : null;
        }
        if (!CanSave || AuthoredAsset.Archetype != null) throw new InvalidOperationException("These asset references can't be edited.");
        var before = revision; var after = ++nextRevision;
        var edit = new PreparedAssetDeletionReferences(AuthoredAsset, deleted, before, after, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = after;
        }, () => revision = before, () => revision = after);
        return edit.HasChanges ? edit : null;
    }
}
