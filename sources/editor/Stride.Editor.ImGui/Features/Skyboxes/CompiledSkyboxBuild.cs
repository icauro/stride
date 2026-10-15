using System.Text.Json;
using Stride.Assets.Effect;
using Stride.Assets.Skyboxes;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record SkyboxTextureSnapshot(SkyboxPreviewDependency Dependency, TextureAsset Asset);
internal sealed record SkyboxShaderSnapshot(SkyboxPreviewDependency Dependency, string? Text);
internal sealed record SkyboxCompilationSnapshot(SkyboxAsset Asset, SkyboxPreviewBuildParameters Parameters,
    IReadOnlyList<SkyboxTextureSnapshot> Textures, IReadOnlyList<SkyboxShaderSnapshot> Shaders);

internal static class CompiledSkyboxBuild
{
    private static string Root => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stride", "Editor.ImGui", "skybox-previews"));
    internal static string Build(SkyboxCompilationSnapshot snapshot, CancellationToken cancellation, Action<ProjectLoadState> progress)
    {
        cancellation.ThrowIfCancellationRequested();
        var output = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        try
        {
            var parameters = snapshot.Parameters; parameters.OutputDirectory = output;
            parameters.BuildDirectory = Path.Combine(Root, "cache", snapshot.Asset.Id.ToString(), "objects");
            parameters.AssetSnapshot = Path.Combine(output, "snapshot.sdsky");
            parameters.RootInput.Snapshot = parameters.AssetSnapshot;
            using (var stream = File.Create(parameters.AssetSnapshot)) AssetFileSerializer.Save(stream, snapshot.Asset, null,
                assetNamespace: parameters.RootInput.AssetNamespace);
            foreach (var texture in snapshot.Textures)
            {
                texture.Dependency.Snapshot = Path.Combine(output, texture.Asset.Id + ".sdtex");
                texture.Dependency.Input.Snapshot = texture.Dependency.Snapshot;
                using (var stream = File.Create(texture.Dependency.Snapshot)) AssetFileSerializer.Save(stream, texture.Asset, null,
                    assetNamespace: texture.Dependency.Input.AssetNamespace);
                parameters.Textures.Add(texture.Dependency);
            }
            foreach (var shader in snapshot.Shaders)
            {
                if (shader.Text != null)
                {
                    var directory = Path.Combine(output, "shaders", shader.Dependency.Id); Directory.CreateDirectory(directory);
                    shader.Dependency.Snapshot = Path.Combine(directory, Path.GetFileName(shader.Dependency.AssetPath));
                    File.WriteAllText(shader.Dependency.Snapshot, shader.Text);
                }
                shader.Dependency.Input.Snapshot = string.IsNullOrEmpty(shader.Dependency.Snapshot) ? shader.Dependency.AssetPath : shader.Dependency.Snapshot;
                parameters.Shaders.Add(shader.Dependency);
            }
            var request = Path.Combine(output, "request.json"); File.WriteAllText(request, JsonSerializer.Serialize(parameters));
            CompiledProjectBuild.Run(AssetCompilerLocator.GetExecutable(), ["preview-skybox", request], output, "skybox", cancellation, progress, "skybox:" + snapshot.Asset.Id);
            if (!File.Exists(Path.Combine(output, "db", "bundles", "default.bundle"))) throw new FileNotFoundException("Skybox compilation produced no content bundle.");
            return output;
        }
        catch { ReleaseOutput(output); throw; }
    }
    internal static void ReleaseOutput(string output)
    {
        var target = Path.GetFullPath(output);
        if (!target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(target), Root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Skybox preview output is outside its owned cache directory.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
