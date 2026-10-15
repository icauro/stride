using Stride.Assets.Entities;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryGetRootAssetDropIndex(AssetId[] assets, EntityHierarchyDocument target, out int index)
    {
        index = target.Asset.Hierarchy.RootParts.Count;
        if (target is not SceneDocument scene) return true;
        var scenes = 0;
        foreach (var id in assets)
            if (workspace?.Session.FindAsset(id)?.Asset is SceneAsset) scenes++;
        if (scenes == 0) return true;
        if (scenes != assets.Length) return false;
        index += scene.Asset.ChildrenIds.Count;
        return true;
    }
}
