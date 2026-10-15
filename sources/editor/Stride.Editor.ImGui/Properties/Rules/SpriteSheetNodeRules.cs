using System;
using Stride.Assets.Sprite;

namespace Stride.Assets.Presentation.NodePresenters.Rules;

internal static class SpriteSheetNodeRules
{
    internal static bool IsFrameMemberVisible(SpriteSheetType type, string name) => type switch
    {
        SpriteSheetType.Sprite2D => name != nameof(SpriteInfo.Borders),
        SpriteSheetType.UI => name != nameof(SpriteInfo.Center) && name != nameof(SpriteInfo.CenterFromMiddle),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
