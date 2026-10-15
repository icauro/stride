using Stride.Core;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPresentedSpriteSheetSettings(AssetMetadataDocument owner)
        => DrawPresentedSpriteSettings(owner, owner.SpriteSheetPresenterRoots);
}
