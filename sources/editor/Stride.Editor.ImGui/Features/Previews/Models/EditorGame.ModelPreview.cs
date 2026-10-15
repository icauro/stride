using Stride.Assets;
using Stride.Assets.Models;
using Stride.Rendering.ProceduralModels;
using Stride.Graphics.GeometricPrimitives;
using Stride.Graphics;
using Stride.Core.Mathematics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ModelAssetPreview? modelPreview;
    private Exception? reportedModelPreviewError;
    internal ModelAssetPreview? ModelPreview => modelPreview;
    private void OpenModelPreview(AssetEntry entry)
    {
        RecordEngineModelPreviewOpening(entry);
        if (projectContent == null) { GetAssetTagDocument(entry); ClearSelectedAssetPreview(); return; }
        CloseCatalogAssetPreviews();
        CloseAnimatedMediaPreviews();
        CloseSkyboxPreview();
        CloseSpriteSheetPreview();
        CloseFontPreview();
        var item = workspace!.FindAsset(entry.Id);
        var compiledUrl = NativeAssetUrls.Qualify(item);
        var settings = item.Package.GetGameSettingsAssetOrDefault();
        var mode = settings.GetOrCreate<EditorSettings>().RenderingMode;
        var colorSpace = settings.GetOrCreate<RenderingSettings>().ColorSpace;
        // The package session can outlive asset saves; use authored on-disk orientation for reopened previews.
        var procedural = item.Asset is ProceduralModelAsset ? Stride.Core.Assets.AssetFileSerializer.Load<ProceduralModelAsset>(entry.Path).Asset : null;
        var defaultAngles = PreviewAngles(procedural?.Type);
        var next = CreateAssetPreview(entry, () => new ModelAssetPreview(this, renderer!, projectContent!, compiledUrl, mode == RenderingMode.HDR,
            procedural != null, defaultAngles, colorSpace));
        if (next == null) return;
        modelPreview?.Dispose(); texturePreview?.Dispose(); texturePreview = null;
        materialPreview?.Dispose(); materialPreview = null;
        modelPreview = next; focusAssetPreview = true; statusMessage = "Previewing " + entry.Name;
        reportedModelPreviewError = null;
        selectedAssetPath = entry.Path;
        assetPreviewPath = entry.Path;
    }
    private static Vector2? PreviewAngles(IProceduralModel? type) => type is CubeProceduralModel
        ? new Vector2(MathUtil.PiOverFour, -MathUtil.Pi / 6)
        : type is PlaneProceduralModel plane ? plane.Normal switch
    {
        NormalDirection.UpX => new Vector2(MathUtil.PiOverTwo, -MathUtil.Pi / 6),
        NormalDirection.UpZ => new Vector2(MathUtil.Pi / 8, -MathUtil.Pi / 6),
        _ => new Vector2(MathUtil.PiOverFour, -MathUtil.Pi / 6),
    } : null;
    private void DrawModelPreview()
    {
        var preview = modelPreview!;
        preview.Visible = false;
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        using var previewWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview);
        if (previewWindow.DrawContents)
        {
            if (PreviewModelToolbar.Draw(icons!, Controls, "model-preview/reset")) preview.ResetCamera();
            if (preview.RenderError is { } error && !ReferenceEquals(reportedModelPreviewError, error))
            { reportedModelPreviewError = error; RecordAssetPreviewRenderFailure("Model preview", error); statusMessage = error.Message; }
            var size = Gui.GetContentRegionAvail();
            if (size.X > 0 && size.Y > 0)
            {
                preview.Visible = true;
                try { preview.Resize((int)size.X, (int)size.Y); }
                catch (Exception resizeFailure)
                {
                    if (!ReferenceEquals(reportedModelPreviewError, resizeFailure))
                    { reportedModelPreviewError = resizeFailure; RecordAssetPreviewRenderFailure("Model preview", resizeFailure); statusMessage = resizeFailure.Message; }
                    preview.Visible = false; return;
                }
                var origin = Gui.GetCursorScreenPos();
                if (preview.RenderError == null) Gui.Image(preview.TextureRef, size); Gui.SetCursorScreenPos(origin);
                DrawAssetPreviewCameraInput("##ModelInput", size, preview.Camera);
                if (Controls.Enabled) Controls.Record("model-preview/image");
            }
        }
    }
}
