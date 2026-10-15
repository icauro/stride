using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Stride.Core.Assets;
using Stride.Video.FFmpeg;

namespace Stride.AssetCompiler;

internal static class AssetMediaProbeCompiler
{
    public static int Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<AssetMediaProbeParameters>(File.ReadAllText(requestPath))
            ?? throw new InvalidDataException("Invalid native media probe request.");
        FFmpegUtils.PreloadLibraries(); FFmpegUtils.Initialize();
        var result = new AssetMediaProbeResult
        {
            Files = request.Files.Select(file =>
            {
                using var media = new FFmpegMedia(); media.Open(file);
                return new AssetMediaProbeFile
                {
                    File = Path.GetFullPath(file),
                    Audio = media.Streams.OfType<AudioStream>().Select(track => new AssetMediaAudioTrack { Index = track.Index, SampleRate = track.SampleRate }).ToArray(),
                    VideoDurationTicks = media.Streams.OfType<VideoStream>().FirstOrDefault()?.Duration.Ticks,
                };
            }).ToArray(),
        };
        File.WriteAllText(Path.Combine(request.OutputDirectory, "result.json"), JsonSerializer.Serialize(result));
        return 0;
    }
}
