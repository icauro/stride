using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    public static bool IsSpriteSourceFile(string path) => AssetRegistry.FindImporterForFile(path)
        .Any(importer => importer.RootAssetTypes.Contains(typeof(SpriteSheetAsset)));
    public void ImportSpriteFiles(IReadOnlyList<string> paths, int insertion = -1)
    {
        EnsureSpriteSettingsGraph();
        if (!CanEditSpriteSettings) throw new InvalidOperationException("This sprite sheet can't be edited.");
        var files = paths.Select(System.IO.Path.GetFullPath).ToArray();
        foreach (var file in files)
            if (!File.Exists(file) || !IsSpriteSourceFile(file)) throw new InvalidOperationException("No sprite image importer is registered for: " + file);
        var values = files.Select((file, index) => new SpriteInfo
        { Name = $"Frame {SpriteSettings!.Sprites.Count + index}", Source = new UFile(file), PixelsPerUnit = 100, CenterFromMiddle = true }).ToArray();
        InsertSpriteFrames(values, insertion, "Import images");
    }
}
