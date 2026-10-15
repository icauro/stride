using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RequestFactoryAssetSources(Package package, string parent, AssetCreationPanel.Choice choice, string fallbackName)
    {
        if (workspace == null || !CanAddAsset || !IsCurrentEditableAssetPackage(package))
            throw new InvalidOperationException("The asset creation target is no longer available.");
        var owner = workspace;
        var files = PickAssetTemplateSources(package, FactorySourceExtensions(choice));
        if (files.Length == 0)
        {
            AssetCreation.RequestWithoutSourcePrompt(Stride.Core.DisplayAttribute.GetDisplayName(choice.AssetType) ?? choice.AssetType.Name, retry =>
            {
                if (!ReferenceEquals(workspace, owner) || !CanAddAsset || !IsCurrentEditableAssetPackage(package)) throw new InvalidOperationException("The asset creation target is no longer available.");
                if (retry) RequestFactoryAssetSources(package, parent, choice, fallbackName);
                else CreateFactoryAsset(package, parent, choice, fallbackName);
            });
            return;
        }
        if (!ReferenceEquals(workspace, owner) || !CanAddAsset || !IsCurrentEditableAssetPackage(package) || !ReferenceEquals(package.Session, owner.Session))
            throw new InvalidOperationException("The asset creation target is no longer available.");
        CreateFactoryAssetsFromSources(package, parent, choice, fallbackName, files);
    }
    private void CreateFactoryAssetsFromSources(Package package, string parent, AssetCreationPanel.Choice choice, string fallbackName, string[] files, bool resourceSourcesPrepared = false)
    {
        if (!resourceSourcesPrepared && RequestResourceSourceCopies(files, package, parent, copied => CreateFactoryAssetsFromSources(package, parent, choice, fallbackName, copied, true))) return;
        if (!CanAddAsset || !IsCurrentEditableAssetPackage(package) || files.Length == 0)
            throw new InvalidOperationException("The asset creation target is no longer available.");
        foreach (var file in files)
            if (!File.Exists(file)) throw new FileNotFoundException("The source file isn't available.", file);
        if (choice.AssetType == typeof(Stride.Assets.Sprite.SpriteSheetAsset))
        {
            CreateSpriteSheetFromSources(package, parent, choice, fallbackName, files); return;
        }
        if (choice.AssetType == typeof(Stride.Audio.Assets.SoundAsset) || choice.AssetType == typeof(Stride.Video.Assets.VideoAsset))
        { RequestMediaFactorySources(files, package, parent, choice); return; }
        if (choice.AssetType == typeof(AnimationAsset) || choice.AssetType == typeof(SkeletonAsset))
        {
            RequestAssetImportFiles(files, package, parent, choice.AssetType, resourceSourcesPrepared: true);
            return;
        }
        if (choice.AssetType == typeof(ModelAsset))
        {
            RequestAssetImportOptions(files, package, parent, choice.AssetType, resourceSourcesPrepared: true);
            return;
        }
        if (!typeof(IAssetWithSource).IsAssignableFrom(choice.AssetType))
        {
            RequestAssetImportOptions(files, package, parent, choice.AssetType, resourceSourcesPrepared: true);
            return;
        }
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared = files.Select(file =>
        {
            var name = Path.GetFileNameWithoutExtension(file);
            return PrepareFactoryAsset(package, parent, choice, IsValidAssetLeafName(name) ? name : fallbackName, new UFile(file), reserved: reserved);
        }).ToArray();
        RequestFactorySourcePublication(package, prepared, "Create assets", () =>
        {
            foreach (var asset in prepared) CreatePreparedAsset(package, asset.Item, asset.Path, "Create assets");
            SelectCreatedAssetBatch(prepared.Select(asset => asset.Item.Id));
        });
    }
}
