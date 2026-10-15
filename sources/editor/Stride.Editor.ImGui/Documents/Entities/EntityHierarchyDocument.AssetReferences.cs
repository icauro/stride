using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument : IAssetReferenceDocument
{
    public PreparedAssetReferenceEdit? PrepareReferenceLocations(IReadOnlyDictionary<AssetId, string> locations,
        string? previousPath = null, string? nextPath = null)
    {
        EndEdits();
        if (!CanSave) throw new InvalidOperationException("This asset can't be edited.");
        if (SessionHistory != null) return NativeAssetReferencePreparation.Prepare(HierarchyGraph, locations, previousPath, nextPath, revision, () => revision,
            (node, index) => CanEditHierarchyNode(node)
                && (node is not Stride.Core.Quantum.IMemberNode member || AssetMetadataDocument.CanAssignAuthoringNode(member))
                && (node is not Stride.Core.Assets.Quantum.IAssetObjectNode collection || !HierarchyGraph.IsInheritedNodeSealed(collection, index)),
            ApplyPreparedReferenceMutation);
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        var edit = new PreparedAssetReferenceEdit(Asset, locations, previousPath, nextPath, beforeRevision, afterRevision, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = afterRevision; LastAction = "Update asset references";
        }, () => revision = beforeRevision, () => revision = afterRevision);
        return edit.HasChanges ? edit : null;
    }
}
