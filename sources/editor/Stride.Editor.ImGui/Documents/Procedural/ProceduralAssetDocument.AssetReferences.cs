using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument : IAssetReferenceDocument
{
    public PreparedAssetReferenceEdit? PrepareReferenceLocations(IReadOnlyDictionary<AssetId, string> locations,
        string? previousPath = null, string? nextPath = null)
    {
        EndEdit();
        if (!CanSave) throw new InvalidOperationException("This asset can't be edited.");
        if (SessionHistory != null) return PrepareNativeReferenceLocations(locations, previousPath, nextPath);
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        var edit = new PreparedAssetReferenceEdit(Asset, locations, previousPath, nextPath, beforeRevision, afterRevision, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = afterRevision;
        }, () => revision = beforeRevision, () => revision = afterRevision);
        return edit.HasChanges ? edit : null;
    }
}
