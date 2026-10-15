using Stride.Core;
using Stride.Graphics;

namespace Stride.Assets.SpriteFont;

/// <summary>Isolated production font build request carrying the real authored origin.</summary>
public sealed class FontPreviewBuildParameters
{
    public string AssetSnapshot { get; set; } = "";
    public string AssetPath { get; set; } = "";
    public string Url { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string BuildDirectory { get; set; } = "";
    public PlatformType Platform { get; set; } = PlatformType.Windows;
    public GraphicsPlatform GraphicsPlatform { get; set; }
    public GraphicsProfile GraphicsProfile { get; set; }
    public ColorSpace ColorSpace { get; set; }
}
