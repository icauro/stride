using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.TextureConverter;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void CreateSpriteSheetFromSources(Package package, string parent, AssetCreationPanel.Choice choice, string name, string[] files)
    {
        var asset = (SpriteSheetAsset)choice.Factory.New();
        foreach (var file in files)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("The sprite source isn't available.", file);
            asset.Sprites.Add(new SpriteInfo { Name = Path.GetFileNameWithoutExtension(file), Source = new UFile(Path.GetFullPath(file)) });
        }
        var prepared = PrepareFactoryAsset(package, parent, choice, name, preparedValue: asset);
        RequestFactorySourcePublication(package, [prepared], "Create asset '" + name + "'", () =>
        {
            CreatePreparedAsset(package, prepared.Item, prepared.Path, "Create asset '" + name + "'");
            SelectCreatedAssetBatch([prepared.Item.Id]);
        }, prepare: () =>
        {
            var log = new LoggerResult();
            using (var tool = new TextureTool())
                for (var index = 0; index < files.Length; index++)
                {
                    var width = 0; var height = 0;
                    try { using var image = tool.Load(files[index], false); width = image.Width; height = image.Height; }
                    catch (Exception error) { log.Warning($"Unable to retrieve the size of \"{files[index]}\": {error.Message}"); }
                    asset.Sprites[index].TextureRegion = new Rectangle(0, 0, width, height);
                }
            foreach (var message in log.Messages) outputLog.Append("Import", message.Type, "Sprite sheet", message.Text);
        });
    }
}
