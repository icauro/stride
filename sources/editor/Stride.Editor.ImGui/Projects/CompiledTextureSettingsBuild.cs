using System.Text.Json;
using Stride.Assets.Textures;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Compiles unsaved settings with the actual import command in a separate compiler process.</summary>
internal static class CompiledTextureSettingsBuild
{
    public static void ReleaseOutput(string output)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "texture-previews"));
        var target = Path.GetFullPath(output);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(target) != root || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Texture preview output is outside the owned cache directory.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
    public static string Build(TextureAsset snapshot, TexturePreviewBuildParameters parameters,
        CancellationToken cancellation, Action<ProjectLoadState> progress)
    {
        cancellation.ThrowIfCancellationRequested();
        var compiler = AssetCompilerLocator.GetExecutable();
        var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "texture-previews", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        parameters.OutputDirectory = output;
        parameters.BuildDirectory = Path.Combine(Path.GetDirectoryName(output)!, "cache", snapshot.Id.ToString(), "objects");
        parameters.AssetSnapshot = Path.Combine(output, "snapshot.sdtex");
        using (var stream = File.Create(parameters.AssetSnapshot)) AssetFileSerializer.Save(stream, snapshot, null);
        var request = Path.Combine(output, "request.json"); File.WriteAllText(request, JsonSerializer.Serialize(parameters));
        CompiledProjectBuild.Run(compiler, ["preview-texture", request], output, "texture", cancellation, progress, "texture:" + snapshot.Id);
        if (!File.Exists(Path.Combine(output, "db", "bundles", "default.bundle"))) throw new FileNotFoundException("Texture import produced no content bundle.", output);
        return output;
    }
}
