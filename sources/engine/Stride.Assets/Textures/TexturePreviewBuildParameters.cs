using Stride.Core;
using Stride.Graphics;

namespace Stride.Assets.Textures;

/// <summary>Isolated texture-preview compiler inputs; asset settings live in the snapshot file.</summary>
public sealed class TexturePreviewBuildParameters
{
    public string AssetSnapshot { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Url { get; set; } = "";
    public string BuildDirectory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public PlatformType Platform { get; set; }
    public GraphicsPlatform GraphicsPlatform { get; set; }
    public GraphicsProfile GraphicsProfile { get; set; }
    public TextureQuality TextureQuality { get; set; }
    public ColorSpace ColorSpace { get; set; }
}
