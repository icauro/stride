using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal byte[] CaptureCompilationAsset(AssetItem canonicalItem, bool excludeCompositorLayout = false)
    {
        if (canonicalItem.Id != AuthoredAsset.Id) throw new InvalidOperationException("The compiler snapshot has another authored owner.");
        // Snapshot the current canonical values and real Quantum YAML metadata
        // without committing or splitting the user's active edit transaction.
        PrepareAssetGraphMetadata();
        PrepareTextureSettingsForSave(); PrepareMaterialSettingsForSave(); PrepareGameSettingsForSave();
        PrepareSpriteSettingsForSave(); PrepareSkyboxSettingsForSave();
        var metadata = authoringItem?.YamlMetadata ?? skyboxSettingsItem?.YamlMetadata ?? spriteSettingsItem?.YamlMetadata ?? gameSettingsItem?.YamlMetadata
            ?? materialSettingsItem?.YamlMetadata ?? textureSettingsItem?.YamlMetadata ?? loaded.YamlMetadata;
        if (excludeCompositorLayout && AuthoredAsset is Stride.Assets.Rendering.GraphicsCompositorAsset)
            return CompositorCompilationSnapshot.Capture(AuthoredAsset, metadata, canonicalItem.Package.Container?.AssetNamespace);
        using var stream = new MemoryStream();
        AssetFileSerializer.Save(stream, AuthoredAsset, metadata, assetNamespace: canonicalItem.Package.Container?.AssetNamespace);
        return stream.ToArray();
    }
}
