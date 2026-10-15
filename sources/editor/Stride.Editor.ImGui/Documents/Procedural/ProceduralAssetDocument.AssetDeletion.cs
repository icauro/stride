using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument : IAssetDeletionDocument
{
    public PreparedAssetDeletionReferences? PrepareDeletedReferences(IReadOnlySet<AssetId> deleted)
    {
        EndEdit();
        if (SessionHistory != null)
        {
            if (!CanSave) throw new InvalidOperationException("These asset references can't be edited.");
            var native = new PreparedAssetDeletionReferences(AuthoringGraph, deleted, revision, () => revision,
                (node, index) => node is Stride.Core.Quantum.IMemberNode member
                    ? CanEditAssetSettingNode(node) && AssetMetadataDocument.CanAssignAuthoringNode(member)
                    : node is Stride.Core.Assets.Quantum.IAssetObjectNode collection && CanEditAssetSettingNode(collection)
                        && !AuthoringGraph.IsInheritedNodeSealed(collection, index),
                mutate => ApplyInheritedAssetMutation(mutate, "Resolve deleted asset references"));
            return native.HasChanges ? native : null;
        }
        if (!CanSave || Asset.Archetype != null) throw new InvalidOperationException("These asset references can't be edited.");
        var before = revision; var after = ++nextRevision;
        var edit = new PreparedAssetDeletionReferences(Asset, deleted, before, after, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = after;
        }, () => revision = before, () => revision = after);
        return edit.HasChanges ? edit : null;
    }
}
