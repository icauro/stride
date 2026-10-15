using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static FileExtensionCollection FactorySourceExtensions(AssetCreationPanel.Choice choice)
    {
        var description = "Source files for " + (DisplayAttribute.GetDisplayName(choice.AssetType) ?? choice.AssetType.Name);
        if (choice.AssetType == typeof(Stride.Physics.Assets.HeightmapAsset) || choice.AssetType == typeof(Stride.Assets.Sprite.SpriteSheetAsset))
            return new FileExtensionCollection(description, Stride.Assets.Textures.TextureImporter.FileExtensions);
        return choice.Template?.GetSupportedExtensions() ?? new FileExtensionCollection(description,
            string.Join(';', AssetRegistry.RegisteredImporters.Where(importer => importer.RootAssetTypes.Contains(choice.AssetType)).Select(importer => importer.SupportedFileExtensions)));
    }

    private string[] PickAssetTemplateSources(Package package, FileExtensionCollection extensions)
    {
        if (TryConsumeTemplateParitySources(package, out var diagnosticSources)) return diagnosticSources;
        var mask = string.Join(';', extensions.SingleExtensions.Select(extension => "*" + extension));
        var directory = ReadAssetImportDialogDirectory(package.RootDirectory.ToOSPath());
        var files = WindowsFileDialogs.OpenMany(GetCurrentDialogOwner(), null,
            (extensions.Description ?? "Files") + "\0" + (mask.Length == 0 ? "*.*" : mask) + "\0All files\0*.*\0\0", directory);
        if (files.Length != 0) RememberAssetImportSelection(files[0]);
        return files;
    }
}
