namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    private PreparedAssetReferenceEdit? PrepareNativeReferenceLocations(IReadOnlyDictionary<Stride.Core.Assets.AssetId, string> locations,
        string? previousPath, string? nextPath)
        => NativeAssetReferencePreparation.Prepare(AuthoringGraph, locations, previousPath, nextPath, revision, () => revision,
            (node, index) => CanEditAssetSettingNode(node)
                && (node is not Stride.Core.Assets.Quantum.IAssetObjectNode collection || !AuthoringGraph.IsInheritedNodeSealed(collection, index)),
            ApplyPreparedAssetMutation);
}
