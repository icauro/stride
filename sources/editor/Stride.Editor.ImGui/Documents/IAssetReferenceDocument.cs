using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal interface IAssetReferenceDocument : IAssetTagDocument
{
    PreparedAssetReferenceEdit? PrepareReferenceLocations(IReadOnlyDictionary<AssetId, string> locations,
        string? previousPath = null, string? nextPath = null);
}
