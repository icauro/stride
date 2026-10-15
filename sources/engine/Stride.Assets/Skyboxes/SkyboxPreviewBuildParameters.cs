using System.Collections.Generic;
using Stride.Core;
using Stride.Graphics;
using Stride.Assets.Textures;
using Stride.Assets.Media;

namespace Stride.Assets.Skyboxes;

public sealed class SkyboxPreviewBuildParameters
{
    public MediaPreviewAssetInput RootInput { get; set; } = new();
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
    public List<SkyboxPreviewDependency> Textures { get; set; } = [];
    public List<SkyboxPreviewDependency> Shaders { get; set; } = [];
}

public sealed class SkyboxPreviewDependency
{
    public MediaPreviewAssetInput Input { get; set; } = new();
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string AssetPath { get; set; } = "";
    public string Snapshot { get; set; } = "";
}
