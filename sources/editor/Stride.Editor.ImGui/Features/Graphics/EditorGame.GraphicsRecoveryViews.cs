using Stride.Assets;
using Stride.Assets.Textures;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action CaptureGraphicsRecoveryPreview(GraphicsRecoveryState state)
    {
        EndAnimationPreviewScrub();
        var texture = texturePreview; var model = modelPreview; var material = materialPreview;
        var sprite = spriteSheetPreview; var font = fontPreview;
        var modelCamera = model?.Camera.CaptureState(); var modelBackdrop = model?.Backdrop;
        var materialCamera = material?.Camera.CaptureState(); var materialPrimitive = material?.Primitive;
        var skyboxCamera = skyboxPreview?.Camera.CaptureState();
        var metalness = skyboxPreview?.Metalness; var glossiness = skyboxPreview?.Glossiness;
        var animationCamera = animationPreview?.Camera.CaptureState();
        var animationTime = animationPreview?.CurrentSeconds; var animationRate = animationPreview?.TimeScale;
        var animationLoop = animationPreview?.Loop; var animationPlaying = animationPreview?.IsPlaying;
        var catalog = catalogPreviewPanel?.CaptureViewRestore();
        var externalRestore = CaptureExternalAssetPreviewRestore();
        state.PreviewKind = animationPreview != null ? "animation" : texture != null ? "texture"
            : model != null ? "model" : material != null ? "material" : skyboxPreview != null ? "skybox"
            : sprite != null ? "sprite" : font != null ? "font" : catalogPreviewPanel?.HasContent == true ? "catalog" : "";
        if (externalPreview != null || requestedCatalogExternal) state.PreviewKind = "external";
        state.PreviewBundle = skyboxPreview?.SourceContent ?? material?.SourceContent ?? model?.SourceContent;
        if (animationPreview != null)
        {
            state.AnimationBundle = animationPreview.SourceContent;
            state.OwnsAnimationBundle = animationPreview.DetachOwnedContentForRecovery() != null;
        }
        return () =>
        {
            if (state.PreviewKind == "external") externalRestore();
            if (texture != null && texturePreview != null) texturePreview.CopyViewFrom(texture);
            if (modelCamera is { } camera && modelPreview != null)
            { modelPreview.Camera.RestoreState(camera); if (modelBackdrop is { } backdrop) modelPreview.SetBackdrop(backdrop); }
            if (materialPrimitive is { } primitive && materialPreview != null) materialPreview.SetPrimitive(primitive);
            if (materialCamera is { } materialView && materialPreview != null) materialPreview.Camera.RestoreState(materialView);
            if (skyboxCamera is { } skyboxView && skyboxPreview != null)
            {
                skyboxPreview.Camera.RestoreState(skyboxView);
                if (metalness is { } metal && glossiness is { } gloss) skyboxPreview.SetMaterial(metal, gloss);
            }
            if (sprite != null && spriteSheetPreview != null) spriteSheetPreview.CopyViewFrom(sprite);
            if (font != null && fontPreview != null) fontPreview.CopyViewFrom(font);
            if (animationTime is { } time && animationPreview != null)
            {
                animationPreview.Seek(time);
                if (animationRate is { } rate) animationPreview.TimeScale = rate;
                if (animationLoop is { } loop) animationPreview.Loop = loop;
                if (animationCamera is { } animationView) animationPreview.Camera.RestoreState(animationView);
                if (animationPlaying == true) animationPreview.Play(); else animationPreview.Pause();
            }
            catalog?.Invoke();
        };
    }

    private void RestoreGraphicsRecoveryPreview(GraphicsRecoveryState state)
    {
        // The audio preview contains no GPU resources and remains the same live instance.
        if (soundPreview != null || state.PreviewPath == null || workspace == null || state.PreviewKind == "") return;
        var entry = workspace.Assets.FirstOrDefault(entry => string.Equals(entry.Path, state.PreviewPath, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { assetPreviewPath = null; return; }
        if (state.PreviewKind == "external")
        {
            if (!state.PreviewCreated)
            {
                state.RestorePreviewView?.Invoke();
                state.PreviewCreated = true;
            }
            assetPreviewPath = entry.Path; selectedAssetPath = state.SelectedAssetPath;
            focusAssetPreview = false;
            return;
        }
        if (state.PreviewCreated)
        {
            state.RestorePreviewView?.Invoke(); selectedAssetPath = state.SelectedAssetPath;
            focusAssetPreview = false; return;
        }
        var item = workspace.FindAsset(entry.Id); var url = item.Location.ToString();
        var settings = item.Package.GetGameSettingsAssetOrDefault();
        var rendering = settings.GetOrDefault<RenderingSettings>();
        var hdr = settings.GetOrDefault<EditorSettings>().RenderingMode == RenderingMode.HDR;
        if (state.AnimationBundle != null)
        {
            var snapshot = CaptureMediaPreview(entry);
            if (snapshot.ModelUrl == null) throw new InvalidOperationException("The animation preview model is unavailable.");
            animationPreview = new AnimationAssetPreview(this, renderer!, state.AnimationBundle, snapshot.ModelUrl,
                snapshot.ClipUrl, snapshot.Hdr, snapshot.Parameters.ColorSpace, state.OwnsAnimationBundle);
            state.AnimationBundle = null; state.OwnsAnimationBundle = false;
            requestedMediaPreview = entry; mediaPreviewWorkspace = workspace;
        }
        else if (state.PreviewKind == "texture")
        {
            var source = TexturePreviewSource(entry.Path, (TextureAsset)item.Asset);
            texturePreview = new TextureAssetPreview(this, renderer!, source.Content, url, source.Asset, rendering.ColorSpace);
            activeTexturePreviewContent = source.Content;
        }
        else if (state.PreviewKind == "material") materialPreview = CreateAuthoredMaterialPreview(entry);
        else if (state.PreviewKind == "skybox")
            skyboxPreview = new SkyboxAssetPreview(this, renderer!, state.PreviewBundle ?? projectContent!, url, hdr, rendering.ColorSpace);
        else if (state.PreviewKind == "sprite") spriteSheetPreview = CreateSpriteSheetPreview(entry);
        else if (state.PreviewKind == "font") fontPreview = CreateFontPreview(entry);
        else if (state.PreviewKind == "model")
            modelPreview = new ModelAssetPreview(this, renderer!, state.PreviewBundle ?? projectContent!, url, hdr,
                typeof(Stride.Assets.Models.ProceduralModelAsset).IsAssignableFrom(entry.AssetType), colorSpace: rendering.ColorSpace);
        else if (typeof(Stride.Assets.Entities.SceneAsset).IsAssignableFrom(entry.AssetType)) OpenSceneAssetPreview(entry);
        else if (typeof(Stride.Assets.Entities.PrefabAsset).IsAssignableFrom(entry.AssetType)) OpenPrefabPreview(entry);
        else if (typeof(Stride.Assets.Models.PrefabModelAsset).IsAssignableFrom(entry.AssetType)) OpenPrefabModelPreview(entry);
        else if (typeof(Stride.Physics.Assets.HeightmapAsset).IsAssignableFrom(entry.AssetType)) OpenHeightmapPreview(entry);
        else if (typeof(Stride.SpriteStudio.Assets.SpriteStudioModelAsset).IsAssignableFrom(entry.AssetType)) OpenSpriteStudioSheetPreview(entry);
        if (state.PreviewKind == "catalog" && catalogPreviewPanel?.HasContent != true)
            throw new InvalidOperationException("The asset preview could not be recreated.");
        state.PreviewCreated = true;
        assetPreviewPath = entry.Path;
        state.RestorePreviewView?.Invoke();
        selectedAssetPath = state.SelectedAssetPath;
        focusAssetPreview = false;
    }

    private void ClearEditorGraphicsRecovery()
    {
        if (editorGraphicsRecovery is { } state)
        {
            ClearSceneDocumentGraphicsRecovery(state);
            if (state is { OwnsAnimationBundle: true, AnimationBundle: { } bundle })
            { bundle.Dispose(); state.OwnsAnimationBundle = false; }
            state.RestorePreviewView = null;
        }
        editorGraphicsRecovery = null;
    }
}
