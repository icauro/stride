using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Assets.Entities;
using Stride.Assets.Models;
using Stride.Physics.Assets;
using Stride.Core.Assets;
using Stride.Games;
using Stride.Graphics;
using Stride.SpriteStudio.Assets;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private CatalogAssetPreviewPanel? catalogPreviewPanel;
    private CatalogAssetPreviewPanel CatalogPreviewPanel => catalogPreviewPanel ??= new(new CatalogAssetPreviewHost(this));
    private void OpenPrefabPreview(AssetEntry entry) => OpenCatalogEntityPreview(entry, "prefab-preview", EntityAssetPreview.Prefab);
    private void OpenPrefabModelPreview(AssetEntry entry) => OpenCatalogEntityPreview(entry, "prefab-model-preview", EntityAssetPreview.PrefabModel);
    private void OpenSpriteStudioSheetPreview(AssetEntry entry) => OpenCatalogEntityPreview(entry, "spritestudio-preview", EntityAssetPreview.SpriteStudio);

    private void OpenCatalogEntityPreview(AssetEntry entry, string kind,
        Func<Stride.Engine.Game, ImGuiRenderer, CompiledProjectContent, string, bool, ColorSpace, PackageTypeScope?, EntityAssetPreview> create)
    {
        RequestCatalogPreviewUpdate(entry);
        var item = workspace!.FindAsset(entry.Path);
        var settings = item.Package.GetGameSettingsAssetOrDefault();
        var next = CreateAssetPreview(entry, () => create(this, renderer!, projectContent ?? throw new InvalidOperationException("The project has not been compiled."),
            item.Location.ToString(), (settings.Defaults.OfType<EditorSettings>().FirstOrDefault()?.RenderingMode ?? RenderingMode.HDR) == RenderingMode.HDR,
            settings.Defaults.OfType<RenderingSettings>().FirstOrDefault()?.ColorSpace ?? ColorSpace.Linear, PackageTypeScope.For(item.Package)));
        if (next == null) return;
        CloseLegacyAssetPreviews();
        CatalogPreviewPanel.SetEntity(next, kind);
        selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true; showAssetPreview = true;
    }

    private void OpenHeightmapPreview(AssetEntry entry)
    {
        RequestCatalogPreviewUpdate(entry);
        var item = workspace!.FindAsset(entry.Path);
        var colorSpace = item.Package.GetGameSettingsAssetOrDefault().Defaults.OfType<RenderingSettings>().FirstOrDefault()?.ColorSpace ?? ColorSpace.Linear;
        var next = CreateAssetPreview(entry, () => new HeightmapAssetPreview(this, renderer!, projectContent ?? throw new InvalidOperationException("The project has not been compiled."), item.Location.ToString(), colorSpace));
        if (next == null) return;
        CloseLegacyAssetPreviews();
        CatalogPreviewPanel.SetHeightmap(next);
        selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true; showAssetPreview = true;
    }

    private void OpenSceneAssetPreview(AssetEntry entry)
    {
        if (!typeof(SceneAsset).IsAssignableFrom(entry.AssetType)) throw new ArgumentException("The selected asset is not a scene.", nameof(entry));
        StopCatalogPreviewUpdate();
        CloseLegacyAssetPreviews();
        CatalogPreviewPanel.ShowSceneNotice();
        selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true; showAssetPreview = true;
    }


    private void ResetCatalogAssetPreviewVisibility() => catalogPreviewPanel?.ResetVisibility();
    private void RenderCatalogAssetPreviews(GameTime time) => catalogPreviewPanel?.Render(time);
    private void CloseCatalogAssetPreviews(bool immediate = false)
    { EndAssetPreviewCameraCapture(); StopCatalogPreviewUpdate(); CloseExternalAssetPreview(); catalogPreviewPanel?.Close(immediate); }
    private bool TryDrawCatalogAssetPreview() => catalogPreviewPanel?.Draw(ref showAssetPreview, ref focusAssetPreview) == true;

    private void CloseLegacyAssetPreviews()
    {
        EndAssetPreviewCameraCapture();
        CloseExternalAssetPreview();
        CloseAnimatedMediaPreviews();
        CloseSkyboxPreview(); CloseSpriteSheetPreview(); CloseFontPreview();
        if (texturePreview != null) { texturePreview.Visible = false; RetireEditorPreview(texturePreview); texturePreview = null; }
        if (modelPreview != null) { modelPreview.Visible = false; RetireEditorPreview(modelPreview); modelPreview = null; }
        if (materialPreview != null) { materialPreview.Visible = false; RetireEditorPreview(materialPreview); materialPreview = null; }
    }

    private sealed class CatalogAssetPreviewHost(EditorGame editor) : ICatalogAssetPreviewHost
    {
        public EditorIcons Icons => editor.icons!;
        public UiControls Controls => editor.Controls;
        public void ReleasePreview(IDisposable preview, bool immediate) => editor.ReleaseCatalogPreviewResource(preview, immediate);
        public void ReportFailure(string kind, Exception error) { editor.RecordAssetPreviewRenderFailure(kind, error); editor.statusMessage = error.Message; }
        public void RegisterCaptureItem(string id, string kind, string name) => editor.RegisterVisualCaptureItem(id, kind, name);
        public void DrawSelectableText(string id, string text) => editor.DrawSelectableText(id, text);
        public void DrawCameraInput(string id, System.Numerics.Vector2 size, Stride.Engine.AssetPreviewCamera camera)
            => editor.DrawAssetPreviewCameraInput(id, size, camera);
        public bool DrawToolbarIcon(string name, float rowY, float rowHeight)
            => editor.DrawPreviewToolbarIcon(name, rowY, rowHeight);
    }
}
