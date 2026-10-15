using System.Text.Json;
using Stride.Assets.Sprite;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Runs the actual sprite-sheet compiler using a canonical authored snapshot.</summary>
internal static class CompiledSpriteSheetBuild
{
    public static SpriteSheetAsset Capture(IAssetTagDocument document)
    {
        if (document.AuthoredAsset is not SpriteSheetAsset sheet)
            throw new InvalidOperationException("The retained document does not contain a sprite sheet.");
        // Compile the published authored graph, including unsaved settings, without ending
        // an active editor transaction or writing its YAML back to the user's project.
        return AssetCloner.Clone(sheet);
    }

    private static string Root => Path.GetFullPath(Path.Combine(EditorUserDataPaths.OverrideRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride"),
        "Editor.ImGui", "sprite-sheet-previews"));

    public static void ReleaseOutput(string output)
    {
        var target = Path.GetFullPath(output);
        var root = Root;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Sprite-sheet preview output is outside the owned cache directory.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    public static string Build(SpriteSheetAsset snapshot, SpriteSheetPreviewBuildParameters parameters,
        CancellationToken cancellation, Action<ProjectLoadState> progress)
    {
        cancellation.ThrowIfCancellationRequested();
        var compiler = AssetCompilerLocator.GetExecutable();
        var output = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        parameters.OutputDirectory = output;
        parameters.BuildDirectory = Path.Combine(output, "objects");
        parameters.AssetSnapshot = Path.Combine(output, "snapshot.sdsheet");
        using (var stream = File.Create(parameters.AssetSnapshot)) AssetFileSerializer.Save(stream, snapshot, null);
        var request = Path.Combine(output, "request.json"); File.WriteAllText(request, JsonSerializer.Serialize(parameters));
        CompiledProjectBuild.Run(compiler, ["preview-sprite-sheet", request], output, "sprite-sheet", cancellation, progress, "sprite-sheet:" + snapshot.Id);
        if (!File.Exists(Path.Combine(output, "db", "bundles", "default.bundle")))
            throw new FileNotFoundException("Sprite-sheet compilation produced no content bundle.", output);
        return output;
    }
}
