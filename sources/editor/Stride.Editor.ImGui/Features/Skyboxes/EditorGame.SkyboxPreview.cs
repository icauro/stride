using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using SVector2 = Stride.Core.Mathematics.Vector2;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SkyboxAssetPreview? skyboxPreview;
    private Exception? reportedSkyboxPreviewError;
    private void OpenSkyboxPreview(AssetEntry entry)
    {
        CloseCatalogAssetPreviews();
        CloseAnimatedMediaPreviews();
        CloseSpriteSheetPreview();
        CloseFontPreview();
        var metadata = GetAssetTagDocument(entry) as AssetMetadataDocument
            ?? throw new InvalidOperationException("The skybox has no canonical metadata document.");
        if (documents.Find(metadata.Path) is { } retained) ActivateDocument(retained);
        if (projectContent == null) { ClearSelectedAssetPreview(); return; }
        if (skyboxPreview != null) { retiredSkyboxResources.Add((skyboxPreview, frames + 2)); skyboxPreview = null; }
        texturePreview?.Dispose(); texturePreview = null; modelPreview?.Dispose(); modelPreview = null;
        materialPreview?.Dispose(); materialPreview = null;
        requestedSkyboxDocument = metadata; pendingSkyboxRevision = null;
        // A retry after a failed build is requested by reopening its real asset.
        attemptedSkyboxRevisions.Remove(metadata);
        selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true; showAssetPreview = true;
        reportedSkyboxPreviewError = null;
    }
    private void DrawSkyboxPreview()
    {
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        var visible = Gui.Begin(PreviewWindowCaption.Text, ref showAssetPreview);
        try
        {
            if (!visible || skyboxPreview is not { } preview) return;
            DrawSkyboxPreviewToolbar(preview);
            if (preview.RenderError is { } error && !ReferenceEquals(error, reportedSkyboxPreviewError))
            { reportedSkyboxPreviewError = error; RecordAssetPreviewRenderFailure("Skybox preview", error); statusMessage = error.Message; }
            var size = Gui.GetContentRegionAvail();
            if (size.X > 0 && size.Y > 0)
            {
                try { preview.Resize((int)size.X, (int)size.Y); }
                catch (Exception resizeError) { RecordAssetPreviewRenderFailure("Skybox preview", resizeError); statusMessage = resizeError.Message; return; }
                preview.Visible = true;
                var origin = Gui.GetCursorScreenPos();
                if (preview.RenderError == null) Gui.Image(preview.TextureRef, size);
                Gui.SetCursorScreenPos(origin); DrawAssetPreviewCameraInput("##SkyboxInput", size, preview.Camera);
                if (Gui.IsItemHovered() && Gui.IsKeyPressed(ImGuiKey.F, false)) preview.ResetCamera();
            }
        }
        finally { ImGuiScopes.EndWindow(); }
    }
    private void DrawSkyboxPreviewToolbar(SkyboxAssetPreview preview)
    {
        var margin = UiScale(6); var gap = UiScale(3);
        var resetHeight = UiScale(16) + Gui.GetStyle().FramePadding.Y * 2;
        var rowHeight = Math.Max(UiScale(16), Math.Max(MeasureNativeLabel("Metalness").Y, MeasureNativeLabel("Glossiness").Y));
        Gui.BeginChild("##SkyboxPreviewToolbar", new NVector2(0, margin * 2 + resetHeight + rowHeight * 2 + gap),
            ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try
        {
        var origin = Gui.GetCursorScreenPos() + new NVector2(margin);
        var width = Math.Max(0, Gui.GetContentRegionAvail().X - margin * 2 - UiScale(70));
        Gui.SetCursorScreenPos(origin);
        if (icons!.Button("camera-reset", EditorLocalization.Text("Reset the camera", "ToolTip"), 16)) preview.ResetCamera();
        for (var row = 0; row < 2; ++row)
        {
            var position = origin + new NVector2(0, resetHeight + row * (rowHeight + gap));
            Gui.SetCursorScreenPos(position);
            DrawNativeLabel(row == 0 ? "Metalness" : "Glossiness");
            Gui.SetCursorScreenPos(position + new NVector2(UiScale(70), (rowHeight - UiScale(16)) * .5f));
            if (width <= 0) { Gui.Dummy(new NVector2(0, UiScale(16))); continue; }
            var value = row == 0 ? preview.Metalness : preview.Glossiness;
            Gui.SetNextItemWidth(width);
            var changed = PreviewSliderControl.DrawFloat(preview, row == 0 ? "##SkyboxMetalness" : "##SkyboxGlossiness", ref value, 0, 1);
            PreviewSliderControl.DrawTicks(0, 1, .0333, false);
            if (changed)
            {
                if (row == 0) Execute(() => preview.SetMaterial(value, preview.Glossiness));
                else Execute(() => preview.SetMaterial(preview.Metalness, value));
            }
        }
        }
        finally { ImGuiScopes.EndChild(); }
    }
}
