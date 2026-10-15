namespace Stride.Core.Assets;

public sealed class AssetMediaProbeParameters
{
    public string[] Files { get; set; } = [];
    public string OutputDirectory { get; set; } = "";
}
public sealed class AssetMediaProbeResult
{
    public AssetMediaProbeFile[] Files { get; set; } = [];
}
public sealed class AssetMediaProbeFile
{
    public string File { get; set; } = "";
    public AssetMediaAudioTrack[] Audio { get; set; } = [];
    public long? VideoDurationTicks { get; set; }
}
public sealed class AssetMediaAudioTrack
{
    public int Index { get; set; }
    public int SampleRate { get; set; }
}
