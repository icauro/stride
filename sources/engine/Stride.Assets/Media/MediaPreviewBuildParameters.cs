using Stride.Core;
using Stride.Graphics;
using Stride.Assets.Textures;

namespace Stride.Assets.Media;

/// <summary>Production media compiler inputs retaining actual source origins and authored asset identities.</summary>
public sealed class MediaPreviewBuildParameters
{
    public string RootId { get; set; } = "";
    public string? GameSettingsId { get; set; }
    public string[] AssemblyPaths { get; set; } = [];
    public string OutputDirectory { get; set; } = "";
    public string BuildDirectory { get; set; } = "";
    public MediaPreviewAssetInput[] Assets { get; set; } = [];
    public PlatformType Platform { get; set; } = PlatformType.Windows;
    public GraphicsPlatform GraphicsPlatform { get; set; }
    public GraphicsProfile GraphicsProfile { get; set; }
    public ColorSpace ColorSpace { get; set; }
    public TextureQuality TextureQuality { get; set; }
}
public sealed class MediaPreviewAssetInput
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string AssetPath { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string? AssetNamespace { get; set; }
    public string LocalUrl { get; set; } = "";
    public string SourcePackagePath { get; set; } = "";
    public string[] PackageDependencies { get; set; } = [];
}
