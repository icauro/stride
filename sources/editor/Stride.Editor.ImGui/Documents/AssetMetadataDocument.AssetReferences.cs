using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument : IAssetReferenceDocument
{
    public PreparedAssetReferenceEdit? PrepareReferenceLocations(IReadOnlyDictionary<AssetId, string> locations,
        string? previousPath = null, string? nextPath = null)
    {
        CommitEdits();
        if (!CanSave) throw new InvalidOperationException("This asset can't be edited.");
        if (SessionHistory != null) return NativeAssetReferencePreparation.Prepare(AuthoringGraph, locations, previousPath, nextPath, revision, () => revision,
            (node, index) => CanEditAssetSettingNode(node)
                && (node is not Stride.Core.Quantum.IMemberNode member || AssetMetadataDocument.CanAssignAuthoringNode(member))
                && (node is not Stride.Core.Assets.Quantum.IAssetObjectNode collection || !AuthoringGraph.IsInheritedNodeSealed(collection, index)),
            ApplyPreparedReferenceMutation);
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        var edit = new PreparedAssetReferenceEdit(AuthoredAsset, locations, previousPath, nextPath, beforeRevision, afterRevision, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = afterRevision;
        }, () => revision = beforeRevision, () => revision = afterRevision);
        return edit.HasChanges ? edit : null;
    }
}
