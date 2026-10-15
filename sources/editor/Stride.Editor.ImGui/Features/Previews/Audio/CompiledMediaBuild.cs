using System.Text.Json;
using Stride.Assets.Media;

namespace Stride.Editor.ImGui;

internal sealed record MediaAssetSnapshot(MediaPreviewAssetInput Input, byte[] Bytes, string Extension);
internal sealed record MediaCompilationSnapshot(MediaPreviewBuildParameters Parameters, MediaAssetSnapshot[] Assets,
    string Revision, string ClipUrl, string? ModelUrl, bool Hdr);

/// <summary>Isolated native compiler job over actual authored media/dependency snapshots.</summary>
internal static class CompiledMediaBuild
{
    private static string Root => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stride", "Editor.ImGui", "media-previews"));
    public static void ReleaseOutput(string output)
    {
        var path = Path.GetFullPath(output);
        if (!string.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(path), "N", out _))
            throw new InvalidOperationException("Media preview output is outside the owned cache directory.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    public static string Build(MediaCompilationSnapshot snapshot, CancellationToken token, Action<ProjectLoadState> progress)
    {
        token.ThrowIfCancellationRequested();
        var output = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        try
        {
            var parameters = snapshot.Parameters;
            parameters.OutputDirectory = output;
            parameters.BuildDirectory = Path.Combine(Root, "cache", parameters.RootId, "objects");
            var inputs = new List<MediaPreviewAssetInput>();
            foreach (var asset in snapshot.Assets)
            {
                token.ThrowIfCancellationRequested();
                var input = asset.Input;
                input.Snapshot = Path.Combine(output, input.Id + asset.Extension);
                File.WriteAllBytes(input.Snapshot, asset.Bytes); inputs.Add(input);
            }
            parameters.Assets = inputs.ToArray();
            var request = Path.Combine(output, "request.json"); File.WriteAllText(request, JsonSerializer.Serialize(parameters));
            CompiledProjectBuild.Run(AssetCompilerLocator.GetExecutable(), ["preview-media", request], output, "media", token, progress, "media:" + snapshot.Parameters.RootId);
            if (!File.Exists(Path.Combine(output, "db", "bundles", "default.bundle")))
                throw new FileNotFoundException("Media compilation produced no content bundle.", output);
            return output;
        }
        catch { ReleaseOutput(output); throw; }
    }
}
