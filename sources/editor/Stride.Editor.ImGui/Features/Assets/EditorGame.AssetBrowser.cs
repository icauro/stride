namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal string? SelectedAssetPath => selectedAssetPath;
    private string? selectedAssetPath;
    private readonly AssetBrowserState assetBrowser = new();
    private AssetBrowserPanel? assetBrowserPanel;
    private AssetBrowserPanel AssetBrowserPanel => assetBrowserPanel ??= new AssetBrowserPanel(new AssetBrowserPanelHost(this), assetBrowser);
    private string assetFilter { get => AssetBrowserPanel.SearchText; set => AssetBrowserPanel.SearchText = value; }
    private void DrawAssets() => AssetBrowserPanel.Draw();
    private void PreviewBrowserAsset(AssetEntry asset)
    {
        selectedAssetPath = asset.Path;
        if (projectContent == null && !typeof(Stride.Assets.Entities.SceneAsset).IsAssignableFrom(asset.AssetType)
            && !HasExternalSourceOnlyPreview(asset.AssetType))
        { ClearSelectedAssetPreview(); return; }
        if (string.Equals(assetPreviewPath, asset.Path, StringComparison.OrdinalIgnoreCase)) return;
        Execute(() =>
        {
            try
            {
                if (TryOpenExternalAssetPreview(asset)) return;
                if (typeof(Stride.Assets.Models.AnimationAsset).IsAssignableFrom(asset.AssetType)) OpenAnimationPreview(asset);
                else if (typeof(Stride.Audio.Assets.SoundAsset).IsAssignableFrom(asset.AssetType)) OpenSoundPreview(asset);
                else if (typeof(Stride.Assets.Entities.SceneAsset).IsAssignableFrom(asset.AssetType)) OpenSceneAssetPreview(asset);
                else if (typeof(Stride.Assets.Entities.PrefabAsset).IsAssignableFrom(asset.AssetType)) OpenPrefabPreview(asset);
                else if (typeof(Stride.Assets.Models.PrefabModelAsset).IsAssignableFrom(asset.AssetType)) OpenPrefabModelPreview(asset);
                else if (typeof(Stride.Physics.Assets.HeightmapAsset).IsAssignableFrom(asset.AssetType)) OpenHeightmapPreview(asset);
                else if (typeof(Stride.SpriteStudio.Assets.SpriteStudioModelAsset).IsAssignableFrom(asset.AssetType)) OpenSpriteStudioSheetPreview(asset);
                else if (typeof(Stride.Assets.Textures.TextureAsset).IsAssignableFrom(asset.AssetType)) OpenTexturePreview(asset);
                else if (typeof(Stride.Assets.Materials.MaterialAsset).IsAssignableFrom(asset.AssetType)) OpenMaterialPreview(asset);
                else if (typeof(Stride.Assets.Skyboxes.SkyboxAsset).IsAssignableFrom(asset.AssetType)) OpenSkyboxPreview(asset);
                else if (typeof(Stride.Assets.SpriteFont.SpriteFontAsset).IsAssignableFrom(asset.AssetType)
                    || typeof(Stride.Assets.SpriteFont.PrecompiledSpriteFontAsset).IsAssignableFrom(asset.AssetType)) OpenFontPreview(asset);
                else if (typeof(Stride.Assets.Sprite.SpriteSheetAsset).IsAssignableFrom(asset.AssetType)) OpenSpriteSheetPreview(asset);
                else if (typeof(Stride.Assets.Models.ModelAsset).IsAssignableFrom(asset.AssetType)
                    || typeof(Stride.Assets.Models.ProceduralModelAsset).IsAssignableFrom(asset.AssetType)) OpenModelPreview(asset);
                else ClearSelectedAssetPreview();
            }
            finally { focusAssetPreview = false; }
        });
    }
    private void OpenBrowserAsset(AssetEntry asset)
    {
        selectedAssetPath = asset.Path;
        Execute(() =>
        {
            if (Controls.Enabled) RecordStockBrowserOpen(asset);
            if (TryOpenRegisteredAssetEditor(asset)) return;
            OpenDefaultBrowserAsset(asset);
        });
    }
    private void OpenDefaultBrowserAsset(AssetEntry asset)
    {
        selectedAssetPath = asset.Path;
        if (typeof(Stride.UI.Assets.UIAssetBase).IsAssignableFrom(asset.AssetType)
            || typeof(Stride.Assets.Rendering.GraphicsCompositorAsset).IsAssignableFrom(asset.AssetType)
            || asset.Extension == ".sdgamesettings"
            || typeof(Stride.Assets.Scripts.VisualScriptAsset).IsAssignableFrom(asset.AssetType)
            || typeof(Stride.Assets.Sprite.SpriteSheetAsset).IsAssignableFrom(asset.AssetType))
        {
            Execute(() =>
            {
                var metadata = GetAssetTagDocument(asset);
                if (documents.Find(metadata) is { } entry) ActivateDocument(entry);
                if (typeof(Stride.Assets.Sprite.SpriteSheetAsset).IsAssignableFrom(asset.AssetType)) OpenSpriteSheetPreview(asset);
            });
            return;
        }
        if (asset.Extension == ".sdscene") Execute(() => OpenScene(asset.Path));
        else if (asset.Extension == ".sdprefab") Execute(() => OpenPrefab(asset.Path));
        else if (typeof(Stride.Core.Assets.SourceCodeAsset).IsAssignableFrom(asset.AssetType)) Execute(() => OpenScriptDocument(asset.Path));
        else if (asset.Extension == ".sdtex") Execute(() =>
        {
            if (!workspace!.FindAsset(asset.Path).Package.IsReadOnly)
            {
                var metadata = GetAssetTagDocument(asset);
                if (documents.Find(metadata) is { } entry && !ReferenceEquals(documents.Active, entry)) ActivateDocument(entry);
            }
            OpenTexturePreview(asset);
        });
        else if (typeof(Stride.Assets.Materials.MaterialAsset).IsAssignableFrom(asset.AssetType)) Execute(() => OpenMaterialPreview(asset));
        else if (typeof(Stride.Assets.Skyboxes.SkyboxAsset).IsAssignableFrom(asset.AssetType)) Execute(() => OpenSkyboxPreview(asset));
        else if (typeof(Stride.Assets.SpriteFont.SpriteFontAsset).IsAssignableFrom(asset.AssetType)
            || typeof(Stride.Assets.SpriteFont.PrecompiledSpriteFontAsset).IsAssignableFrom(asset.AssetType)) Execute(() => OpenFontPreview(asset));
        else if (typeof(Stride.Assets.Models.ProceduralModelAsset).IsAssignableFrom(asset.AssetType)) Execute(() =>
        {
            OpenAssetEditor(asset.Path);
            OpenModelPreview(asset);
        });
        else if (typeof(Stride.Assets.Models.ModelAsset).IsAssignableFrom(asset.AssetType)) Execute(() => OpenModelPreview(asset));
        else statusMessage = "Selected " + asset.Name;
    }
}
