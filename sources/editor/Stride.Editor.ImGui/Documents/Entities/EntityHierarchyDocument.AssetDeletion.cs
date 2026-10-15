using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument : IAssetDeletionDocument
{
    public PreparedAssetDeletionReferences? PrepareDeletedReferences(IReadOnlySet<AssetId> deleted)
    {
        EndEdits();
        if (SessionHistory != null)
        {
            if (!CanSave) throw new InvalidOperationException("These asset references can't be edited.");
            var native = new PreparedAssetDeletionReferences(HierarchyGraph, deleted, revision, () => revision,
                (node, index) => node is Stride.Core.Quantum.IMemberNode
                    ? CanEditHierarchyNode(node) && node is Stride.Core.Assets.Quantum.IAssetMemberNode { CanOverride: true }
                    : node is Stride.Core.Assets.Quantum.IAssetObjectNode collection && CanEditHierarchyItem(collection, index),
                mutate => ApplyInheritedGraphMutation(mutate, "Resolve deleted asset references"));
            return native.HasChanges ? native : null;
        }
        if (!CanSave || Asset.Archetype != null || HasInheritedHierarchy) throw new InvalidOperationException("These asset references can't be edited.");
        var before = revision; var after = ++nextRevision;
        var edit = new PreparedAssetDeletionReferences(Asset, deleted, before, after, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = after; LastAction = "Resolve deleted asset references";
        }, () => revision = before, () => revision = after);
        return edit.HasChanges ? edit : null;
    }
}
