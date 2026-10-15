using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal AssetRepairContext CreateRepairContext() => new(historyScope.AuthoringState ?? throw new InvalidOperationException("The asset has no canonical owner."),
        AuthoredAsset, AuthoringGraph, CanEditAssetSettingNode, CanEditAssetSettingItem, ApplyInheritedAssetMutation);
}
internal abstract partial class EntityHierarchyDocument
{
    internal AssetRepairContext CreateRepairContext() => new(historyScope.AuthoringState ?? throw new InvalidOperationException("The hierarchy has no canonical owner."),
        Asset, HierarchyGraph, CanEditHierarchyNode, CanEditHierarchyItem, ApplyInheritedGraphMutation);
}
internal sealed partial class ProceduralAssetDocument
{
    internal AssetRepairContext CreateRepairContext() => new(historyScope.AuthoringState ?? throw new InvalidOperationException("The procedural asset has no canonical owner."),
        Asset, AuthoringGraph, CanEditAssetSettingNode,
        (collection, index) => CanEditAssetSettingNode(collection) && !AuthoringGraph.IsInheritedNodeSealed(collection, index),
        ApplyInheritedAssetMutation);
}
