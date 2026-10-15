using System.Text.Json;
using Stride.Assets.Media;
using Stride.Audio.Assets;
using Stride.Video.Assets;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal static partial class AssetSourceImport
{
    internal static AssetSourceImportResult PrepareMediaFactory(string[] files, Asset preset, CancellationToken cancellation)
    {
        if (preset is not SoundAsset and not VideoAsset) throw new ArgumentException("The factory does not create media assets.");
        var snapshots = CaptureSources(files, cancellation);
        var output = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        try
        {
            var request = new AssetMediaProbeParameters { Files = files, OutputDirectory = output };
            var path = Path.Combine(output, "request.json"); File.WriteAllText(path, JsonSerializer.Serialize(request));
            CompiledProjectBuild.Run(AssetCompilerLocator.GetExecutable(), ["probe-media", path], output, "probe", cancellation, null);
            cancellation.ThrowIfCancellationRequested(); ValidateSources(snapshots.SourceHashes);
            var result = JsonSerializer.Deserialize<AssetMediaProbeResult>(File.ReadAllText(Path.Combine(output, "result.json")))
                ?? throw new InvalidDataException("The native media probe returned no stream metadata.");
            if (result.Files.Length != files.Length || result.Files.Select(file => file.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length
                || result.Files.Any(file => !files.Contains(Path.GetFullPath(file.File), StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("The native media probe has different source provenance.");
            var assets = new List<AssetItem>(); var provenance = new Dictionary<AssetId, string>();
            foreach (var file in result.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                var url = Path.GetFileNameWithoutExtension(file.File);
                if (preset is SoundAsset sound)
                    foreach (var track in file.Audio)
                    {
                        var asset = AssetCloner.Clone(sound); asset.Id = AssetId.New(); asset.Source = new UFile(file.File);
                        asset.Index = track.Index; asset.SampleRate = track.SampleRate;
                        assets.Add(new AssetItem(file.Audio.Length > 1 ? url + " track " + track.Index : url, asset)); provenance.Add(asset.Id, file.File);
                    }
                else if (preset is VideoAsset video && file.VideoDurationTicks is { } ticks)
                {
                    var asset = AssetCloner.Clone(video); asset.Id = AssetId.New(); asset.Source = new UFile(file.File);
                    asset.VideoDuration.StartTime = TimeSpan.Zero; asset.VideoDuration.EndTime = TimeSpan.FromTicks(ticks);
                    assets.Add(new AssetItem(url, asset)); provenance.Add(asset.Id, file.File);
                }
            }
            return snapshots with { Assets = assets.ToArray(), AssetSources = provenance };
        }
        finally
        {
            var resolved = Path.GetFullPath(output);
            if (!string.Equals(Path.GetDirectoryName(resolved), Root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                throw new InvalidOperationException("Native media output is outside its owned cache directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
