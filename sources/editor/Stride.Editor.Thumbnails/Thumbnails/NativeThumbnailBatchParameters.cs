using System.Collections.Generic;
using Stride.Assets.Media;
using Stride.Core.Diagnostics;

namespace Stride.Editor.Thumbnails;

// Native resident protocol over immutable certified authored inputs.
public sealed class NativeThumbnailBatchParameters
{
    public string Generation { get; set; } = "";
    public MediaPreviewBuildParameters Inputs { get; set; } = new();
    public NativeThumbnailAssemblyInput[] Assemblies { get; set; } = [];
    public Dictionary<string, string> AssetHashes { get; set; } = new();
    public Dictionary<string, string> SourceHashes { get; set; } = new();
    public NativeThumbnailBatchRoot[] Roots { get; set; } = [];
}

public sealed class NativeThumbnailBatchRoot
{
    public string AssetId { get; set; } = "";
    public string Identity { get; set; } = "";
    public string CompilerType { get; set; } = "";
}

public sealed record NativeThumbnailBatchResult(string AssetId, string Identity,
    ThumbnailBuildResult Result, LogMessageType Level, byte[]? Png, string? Error);
