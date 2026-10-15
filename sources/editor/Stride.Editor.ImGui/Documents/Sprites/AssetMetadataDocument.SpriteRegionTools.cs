using Stride.Assets.Sprite;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    public void SetSpriteFrameRegion(SpriteSettingFrame frame, Rectangle region, bool resetBorders = false)
    {
        var field = frame.Fields.First(candidate => candidate.Node.Name == nameof(SpriteInfo.TextureRegion));
        var sprites = (IAssetObjectNode)spriteSettingsGraph!.RootNode[nameof(SpriteSheetAsset.Sprites)].Target!;
        var borderNode = sprites.IndexedTarget(sprites.IdToIndex(frame.Id))![nameof(SpriteInfo.Borders)];
        if (resetBorders && (!CanEditAssetSettingNode(borderNode) || !CanAssignAuthoringNode(borderNode)))
            throw new InvalidOperationException("These sprite borders cannot be edited.");
        EditSpriteFrames("Update selected region", () =>
        {
            UpdateSpriteSetting(field, region); EndSpriteSettingEdit();
            if (!resetBorders) return;
            var before = borderNode.Retrieve();
            if (Equals(before, Vector4.Zero)) return;
            ApplyInheritedAssetMutation(() => borderNode.Update(Vector4.Zero), "Reset sprite borders");
        });
    }
}
