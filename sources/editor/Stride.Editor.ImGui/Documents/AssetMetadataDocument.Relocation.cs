namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument : IRelocatableEditorDocument
{
    public ReadOnlyMemory<byte> DiskContentHash => diskHash;
    public void RelocatePath(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        DocumentAssetPath.Update(textureSettingsItem, path);
        DocumentAssetPath.Update(materialSettingsItem, path);
        DocumentAssetPath.Update(spriteSettingsItem, path);
        UpdateGameSettingsPath(path);
        UpdateSkyboxSettingsPath(path);
        Path = path;
    }
}
