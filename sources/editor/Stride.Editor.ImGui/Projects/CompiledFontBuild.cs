using System.Text.Json;
using Stride.Assets.SpriteFont;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal static class CompiledFontBuild
{
    private static string Root => Path.Combine(EditorUserDataPaths.OverrideRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride"), "Editor.ImGui", "font-previews");
    public static string Build(Asset snapshot, FontPreviewBuildParameters parameters, CancellationToken token, Action<ProjectLoadState> progress,
        FontCompilationSources? sources = null)
    {
        token.ThrowIfCancellationRequested(); var compiler = AssetCompilerLocator.GetExecutable();
        var output = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        try
        {
        snapshot = sources?.Stage(snapshot, parameters.AssetPath, output) ?? snapshot;
        parameters.OutputDirectory = output; parameters.BuildDirectory = Path.Combine(output, "objects");
        parameters.AssetSnapshot = Path.Combine(output, snapshot is SpriteFontAsset ? "snapshot.sdfnt" : "snapshot.sdpcfnt");
        using (var stream = File.Create(parameters.AssetSnapshot)) AssetFileSerializer.Save(stream, snapshot, null);
        var request = Path.Combine(output, "request.json"); File.WriteAllText(request, JsonSerializer.Serialize(parameters));
        CompiledProjectBuild.Run(compiler, ["preview-font", request], output, "font", token, progress, "font:" + snapshot.Id);
        if (!File.Exists(Path.Combine(output, "db", "bundles", "default.bundle"))) throw new FileNotFoundException("Font compilation produced no content bundle.", output);
        return output;
        }
        catch { ReleaseOutput(output); throw; }
    }
    public static void ReleaseOutput(string output)
    {
        var target = Path.GetFullPath(output); var root = Path.GetFullPath(Root);
        if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Font preview output is outside the owned cache directory.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
