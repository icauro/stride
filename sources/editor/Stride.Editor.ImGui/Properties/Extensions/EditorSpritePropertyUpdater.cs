using Stride.Assets.Sprite;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

// Original SpriteSheetAssetNodeUpdater behavior over the real authored asset.
internal sealed class EditorSpritePropertyUpdater(Asset asset) : NodePresenterUpdaterBase
{
    public override void UpdateNode(INodePresenter node)
    {
        if (asset is not SpriteSheetAsset sheet) return;
        if (node.Name == nameof(SpriteSheetAsset.Sprites) && node.Value is List<SpriteInfo>)
        {
            node.IsVisible = false;
            foreach (var child in node.Children) child.IsVisible = false;
        }
        if (!typeof(SpriteInfo).IsAssignableFrom(node.Type)) return;
        foreach (var child in node.Children)
            if (!SpriteSheetNodeRules.IsFrameMemberVisible(sheet.Type, child.Name)) child.IsVisible = false;
    }
}
