using Stride.Core;
using Stride.Graphics;
using Stride.Assets.Textures;

namespace Stride.Assets.Sprite;

/// <summary>Isolated inputs for the regular sprite-sheet compiler.</summary>
public sealed class SpriteSheetPreviewBuildParameters
{
    public string AssetSnapshot { get; set; } = "";
    public string AssetPath { get; set; } = "";
    public string Url { get; set; } = "";
    public string BuildDirectory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public PlatformType Platform { get; set; }
    public GraphicsPlatform GraphicsPlatform { get; set; }
    public GraphicsProfile GraphicsProfile { get; set; }
    public TextureQuality TextureQuality { get; set; }
    public ColorSpace ColorSpace { get; set; }
}
