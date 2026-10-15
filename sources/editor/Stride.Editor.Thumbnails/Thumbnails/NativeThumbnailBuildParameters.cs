using Stride.Assets.Media;
using System.Collections.Generic;

namespace Stride.Editor.Thumbnails;

/// <summary>Isolated native compiler request over certified authored input snapshots.</summary>
public sealed class NativeThumbnailBuildParameters
{
    public MediaPreviewBuildParameters Inputs { get; set; } = new();
    public NativeThumbnailAssemblyInput[] Assemblies { get; set; } = [];
    public string CompilerType { get; set; } = "";
    public string OutputPng { get; set; } = "";
    public Dictionary<string, string> AssetHashes { get; set; } = new();
    public Dictionary<string, string> SourceHashes { get; set; } = new();
}

public sealed class NativeThumbnailAssemblyInput
{
    public string Path { get; set; } = "";
    public string Identity { get; set; } = "";
    public string ModuleVersionId { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
