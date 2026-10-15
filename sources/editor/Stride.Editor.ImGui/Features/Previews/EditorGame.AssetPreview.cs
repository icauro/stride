using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Assets.Textures;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private TextureAssetPreview? texturePreview;
    private bool focusAssetPreview;
    private bool showAssetPreview = true;
    private string? assetPreviewPath;
    private Exception? reportedTexturePreviewError;
    private static readonly string[] TextureCubeModes = ["Right", "Left", "Top", "Bottom", "Front", "Back", "Full"];
    internal TextureAssetPreview? TexturePreview => texturePreview;
    private void OpenTexturePreview(AssetEntry entry)
    {
        if (projectContent == null) { ClearSelectedAssetPreview(); return; }
        CloseCatalogAssetPreviews();
        CloseAnimatedMediaPreviews();
        CloseSkyboxPreview();
        CloseSpriteSheetPreview();
        CloseFontPreview();
        var item = workspace!.AssetItems.Single(item => string.Equals(item.FullPath.ToOSPath(), entry.Path, StringComparison.OrdinalIgnoreCase));
        var source = TexturePreviewSource(entry.Path, (TextureAsset)item.Asset);
        var colorSpace = item.Package.GetGameSettingsAssetOrDefault().GetOrCreate<RenderingSettings>().ColorSpace;
        var next = CreateAssetPreview(entry, () => new TextureAssetPreview(this, renderer!, source.Content, item.Location.ToString(), source.Asset, colorSpace));
        if (next == null) return;
        reportedTexturePreviewError = null;
        texturePreview?.Dispose(); texturePreview = next; focusAssetPreview = true;
        materialPreview?.Dispose(); materialPreview = null;
        activeTexturePreviewContent = source.Content;
        modelPreview?.Dispose(); modelPreview = null;
        selectedAssetPath = entry.Path;
        assetPreviewPath = entry.Path;
        statusMessage = "Previewing " + entry.Name;
    }
    private void DrawAssetPreview()
    {
        using var cameraFrame = new AssetPreviewCameraFrameScope(this);
        using var scrubFrame = new AnimationPreviewScrubFrameScope(this);
        ResetCatalogAssetPreviewVisibility();
        ResetExternalAssetPreviewVisibility();
        HideAnimatedMediaPreviews();
        if (texturePreview != null) texturePreview.Visible = false;
        if (modelPreview != null) modelPreview.Visible = false;
        if (materialPreview != null) materialPreview.Visible = false;
        if (skyboxPreview != null) skyboxPreview.Visible = false;
        if (spriteSheetPreview != null) spriteSheetPreview.Visible = false;
        if (fontPreview != null) fontPreview.Visible = false;
        if (!showAssetPreview) { EndAssetPreviewCameraCapture(); return; }
        if (assetBrowser.HasSelectionInteraction && (assetBrowser.SelectedCount != 1 || assetBrowser.PrimaryEntry is not { } selected
            || !string.Equals(selected.Path, assetPreviewPath, StringComparison.OrdinalIgnoreCase)))
        {
            EndAssetPreviewCameraCapture();
            using var emptyWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview);
            return;
        }
        if (TryDrawExternalAssetPreview()) return;
        if (TryDrawCatalogAssetPreview()) return;
        if (animationPreview != null || requestedMediaPreview is { } animation && typeof(Stride.Assets.Models.AnimationAsset).IsAssignableFrom(animation.AssetType)) { DrawAnimationPreview(); return; }
        if (soundPreview != null || requestedMediaPreview is { } sound && typeof(Stride.Audio.Assets.SoundAsset).IsAssignableFrom(sound.AssetType)) { DrawSoundPreview(); return; }
        if (materialPreview != null) { DrawMaterialPreview(); return; }
        if (modelPreview != null) { DrawModelPreview(); return; }
        if (skyboxPreview != null || requestedSkyboxDocument != null) { DrawSkyboxPreview(); return; }
        if (spriteSheetPreview != null || requestedSpriteSheetDocument != null) { DrawSpriteSheetPreview(); return; }
        if (fontPreview != null || requestedFontDocument != null) { DrawFontPreview(); return; }
        if (texturePreview == null) return;
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        using var previewWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview);
        if (previewWindow.DrawContents)
        {
            var preview = texturePreview;
            if (preview.RenderError is { } error && !ReferenceEquals(reportedTexturePreviewError, error))
            {
                reportedTexturePreviewError = error;
                statusMessage = error.Message;
                RecordAssetPreviewRenderFailure("Texture preview", error);
            }
            var toolbarHeight = Math.Max(Gui.GetFrameHeight() + UiScale(4),
                Math.Max(UiScale(20) + Gui.GetStyle().FramePadding.Y * 2, Gui.GetFontSize() + UiScale(8)));
            using (ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, NVector2.Zero))
            using (ImGuiScopes.Child("##TexturePreviewToolbar", new NVector2(0, toolbarHeight), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
            var toolbarOrigin = Gui.GetCursorScreenPos();
            var toolbarWidth = Gui.GetContentRegionAvail().X;
            var rowY = toolbarOrigin.Y;
            if (DrawPreviewToolbarIcon("zoom-out", rowY, toolbarHeight)) preview.Zoom(false);
            if (Controls.Enabled) Controls.Record("preview/zoom-out");
            if (DrawPreviewToolbarIcon("zoom-in", rowY, toolbarHeight)) preview.Zoom(true);
            if (Controls.Enabled) Controls.Record("preview/zoom-in");
            if (DrawPreviewToolbarIcon("actual-size", rowY, toolbarHeight)) preview.ActualSize();
            if (Controls.Enabled) Controls.Record("preview/actual-size");
            if (DrawPreviewToolbarIcon("fit", rowY, toolbarHeight)) preview.Fit();
            if (Controls.Enabled) Controls.Record("preview/fit");
            DrawPreviewToolbarSeparator(rowY, toolbarHeight);
            Gui.SetCursorScreenPos(Gui.GetCursorScreenPos() + new NVector2(UiScale(2), 0));
            PlacePreviewToolbarItem(rowY, toolbarHeight, Gui.GetFrameHeight());
            Gui.SetNextItemWidth(UiScale(100));
            var mipOpen = Gui.BeginCombo("##Mip", "Level " + preview.Mip);
            if (Controls.Enabled) Controls.Record("preview/mip");
            if (mipOpen)
            {
                try
                {
                for (var mip = 0; mip < preview.MipCount; mip++)
                {
                    if (Gui.Selectable("Level " + mip, preview.Mip == mip)) preview.Mip = mip;
                    if (Controls.Enabled) Controls.Record("preview/mip/" + mip);
                }
                }
                finally { Gui.EndCombo(); }
            }
            if (preview.Dimension == TextureDimension.TextureCube)
            {
                Gui.SameLine(0, UiScale(4));
                PlacePreviewToolbarItem(rowY, toolbarHeight, Gui.GetFrameHeight());
                Gui.SetNextItemWidth(UiScale(100));
                var modes = TextureCubeModes;
                var cubeOpen = Gui.BeginCombo("##Cube", modes[preview.CubeMode]);
                if (Controls.Enabled) Controls.Record("preview/cube");
                if (cubeOpen)
                {
                    try
                    {
                    for (var mode = 0; mode < modes.Length; mode++)
                    {
                        if (Gui.Selectable(modes[mode], preview.CubeMode == mode)) preview.CubeMode = mode;
                        if (Controls.Enabled) Controls.Record("preview/cube/" + mode);
                    }
                    }
                    finally { Gui.EndCombo(); }
                }
            }
            if (preview.Dimension == TextureDimension.Texture3D)
            {
                Gui.SameLine(0, UiScale(2));
                var panelLeft = Gui.GetCursorScreenPos().X;
                var panelRight = toolbarOrigin.X + toolbarWidth;
                var previousWidth = Gui.CalcTextSize("<").X + UiScale(8);
                var nextWidth = Gui.CalcTextSize(">").X + UiScale(8);
                var nextX = panelRight - UiScale(68) - nextWidth;
                Gui.PushClipRect(new NVector2(panelLeft, rowY), new NVector2(Math.Max(panelLeft, panelRight), rowY + toolbarHeight), true);
                try
                {
                using (ImGuiScopes.Style(ImGuiStyleVar.FramePadding, new NVector2(UiScale(4), 0)))
                {
                Gui.SetCursorScreenPos(new NVector2(panelLeft, rowY));
                if (Gui.Button("<##PreviousSlice", new NVector2(previousWidth, toolbarHeight))) preview.PreviousSlice();
                if (Controls.Enabled) Controls.Record("preview/slice/previous");
                if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Previous slice", "ToolTip"));
                Gui.SetCursorScreenPos(new NVector2(nextX, rowY));
                if (Gui.Button(">##NextSlice", new NVector2(nextWidth, toolbarHeight))) preview.NextSlice();
                if (Controls.Enabled) Controls.Record("preview/slice/next");
                if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Next slice", "ToolTip"));
                }
                var depth = preview.Slice;
                Gui.SetCursorScreenPos(new NVector2(panelLeft + previousWidth, rowY));
                PlacePreviewToolbarItem(rowY, toolbarHeight, UiScale(16));
                Gui.SetNextItemWidth(Math.Max(1, nextX - panelLeft - previousWidth));
                if (PreviewSliderControl.DrawFloat(preview, "##Slice", ref depth, 0, preview.Depth, 1, 5, false)) preview.Slice = depth;
                if (Controls.Enabled) Controls.Record("preview/slice");
                var depthText = $"{preview.Slice:0.##}/{preview.Depth}";
                var depthSize = Gui.CalcTextSize(depthText);
                Gui.PushClipRect(new NVector2(panelRight - UiScale(64), rowY), new NVector2(panelRight - UiScale(4), rowY + toolbarHeight), true);
                try
                {
                    Gui.GetWindowDrawList().AddText(new NVector2(panelRight - UiScale(64) + (UiScale(60) - depthSize.X) / 2,
                        rowY + Math.Max(0, (toolbarHeight - depthSize.Y) / 2)), Gui.GetColorU32(ImGuiCol.Text), depthText);
                }
                finally { Gui.PopClipRect(); }
                }
                finally { Gui.PopClipRect(); }
            }
            }
            var size = Gui.GetContentRegionAvail();
            var footerOrigin = Gui.GetCursorScreenPos() + new NVector2(0, Math.Max(0, size.Y - PreviewImageStatusBar.Height));
            size.Y -= PreviewImageStatusBar.Height;
            if (size.X > 0 && size.Y > 0)
            {
                preview.Visible = true;
                try { preview.Resize((int)size.X, (int)size.Y); }
                catch (Exception resizeFailure)
                {
                    if (!ReferenceEquals(reportedTexturePreviewError, resizeFailure))
                    {
                        reportedTexturePreviewError = resizeFailure;
                        statusMessage = resizeFailure.Message;
                        RecordAssetPreviewRenderFailure("Texture preview", resizeFailure);
                    }
                    preview.Visible = false;
                    Gui.SetCursorScreenPos(footerOrigin);
                    PreviewImageStatusBar.Draw(preview.Scale, preview.Width, preview.Height, false);
                    return;
                }
                var origin = Gui.GetCursorScreenPos();
                if (preview.RenderError == null) Gui.Image(preview.TextureRef, size);
                Gui.SetCursorScreenPos(origin);
                EditorPreviewPointerInput.Draw("##TextureInput", size);
                if (Controls.Enabled) Controls.Record("preview/image");
                if (Gui.IsItemHovered())
                {
                    var io = Gui.GetIO();
                    if (io.MouseWheel != 0) preview.Zoom(io.MouseWheel > 0,
                        new SVector2((io.MousePos.X - origin.X) * preview.TargetWidth / size.X,
                            (io.MousePos.Y - origin.Y) * preview.TargetHeight / size.Y));
                }
                if (EditorPreviewPointerInput.TryPanDelta(out var delta))
                {
                    preview.Pan(new SVector2(delta.X * preview.TargetWidth / size.X, delta.Y * preview.TargetHeight / size.Y));
                }
            }
            Gui.SetCursorScreenPos(footerOrigin);
            PreviewImageStatusBar.Draw(preview.Scale, preview.Width, preview.Height, false);
        }
    }
}
