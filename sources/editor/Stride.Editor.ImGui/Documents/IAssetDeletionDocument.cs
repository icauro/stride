using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal interface IAssetDeletionDocument : IAssetReferenceDocument
{
    PreparedAssetDeletionReferences? PrepareDeletedReferences(IReadOnlySet<AssetId> deleted);
}
