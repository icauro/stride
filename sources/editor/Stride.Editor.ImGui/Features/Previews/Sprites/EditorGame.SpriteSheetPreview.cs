using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Assets.Sprite;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SpriteSheetAssetPreview? spriteSheetPreview;
    private AssetMetadataDocument? requestedSpriteSheetDocument;
    private AssetMetadataDocument? spriteSheetPreviewDocument;
    private Exception? reportedSpriteSheetPreviewError;
    private static readonly string[] SpriteSheetPreviewModes = ["Sprites", "Sprite textures"];
    private void OpenSpriteSheetPreview(AssetEntry entry)
    {
        CloseCatalogAssetPreviews();
        CloseAnimatedMediaPreviews();
        var item = workspace!.FindAsset(entry.Id);
        var authored = GetAssetTagDocument(entry) as AssetMetadataDocument;
        if (projectContent == null) { ClearSelectedAssetPreview(); return; }
        CloseSkyboxPreview();
        CloseFontPreview();
        texturePreview?.Dispose(); texturePreview = null; modelPreview?.Dispose(); modelPreview = null;
        materialPreview?.Dispose(); materialPreview = null;
        CloseSpriteSheetPreview();
        requestedSpriteSheetDocument = authored;
        spriteSheetPreviewDocument = authored;
        selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true;
        var url = item.Location.ToString();
        if (Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(url))
        {
            spriteSheetPreview = CreateAssetPreview(entry, () => CreateSpriteSheetPreview(entry));
            if (spriteSheetPreview != null) requestedSpriteSheetDocument = null;
        }
        if (authored != null && workspace.FindAsset(entry.Id).Package.IsReadOnly == false) WatchSpriteSources(authored);
    }
    private SpriteSheetAssetPreview CreateSpriteSheetPreview(AssetEntry entry)
    {
        var item = workspace!.FindAsset(entry.Id); var url = item.Location.ToString();
        var content = refreshedSpriteSheets.TryGetValue(url, out var refreshed) && HasCurrentSpriteSheetContent(refreshed)
            ? refreshed.Content.Bundle : projectContent!;
        var authored = documents.AuthoredDocuments.OfType<AssetMetadataDocument>().FirstOrDefault(owner => owner.AuthoredAsset.Id == entry.Id);
        var asset = authored?.SpriteSettings ?? (SpriteSheetAsset)item.Asset;
        var space = item.Package.GetGameSettingsAssetOrDefault().GetOrCreate<RenderingSettings>().ColorSpace;
        return new SpriteSheetAssetPreview(this, renderer!, content, url, asset, space);
    }
    private void ReloadSpriteSheetPreview(AssetMetadataDocument document)
    {
        if (!string.Equals(assetPreviewPath, document.Path, StringComparison.OrdinalIgnoreCase)) return;
        var entry = workspace!.Assets.FirstOrDefault(asset => asset.Id == document.AuthoredAsset.Id);
        if (entry == null) return;
        Execute(() =>
        {
            var next = CreateSpriteSheetPreview(entry);
            if (spriteSheetPreview != null) { next.CopyViewFrom(spriteSheetPreview); RetireEditorPreview(spriteSheetPreview); }
            spriteSheetPreview = next; requestedSpriteSheetDocument = null; reportedSpriteSheetPreviewError = null;
            spriteSheetPreviewDocument = document;
        });
    }
    private void CloseSpriteSheetPreview()
    {
        if (spriteSheetPreview != null) { spriteSheetPreview.Visible = false; RetireEditorPreview(spriteSheetPreview); spriteSheetPreview = null; }
        requestedSpriteSheetDocument = spriteSheetPreviewDocument = null; reportedSpriteSheetPreviewError = null;
    }
    private void RebindSpriteSettingsDocument(IEditorDocument previous, IEditorDocument next)
    {
        if (ReferenceEquals(requestedSpriteSheetDocument, previous) || ReferenceEquals(spriteSheetPreviewDocument, previous))
        {
            var owner = next as AssetMetadataDocument;
            if (owner?.SpriteSettings == null) CloseSpriteSheetPreview();
            else
            {
                requestedSpriteSheetDocument = spriteSheetPreviewDocument = owner;
                assetPreviewPath = owner.Path;
                // Rebuild a swapped authored generation even when its fresh edit
                // counter is zero and a previous compiled root still exists.
                compiledSpriteRevisions.Remove(owner);
            }
        }
        if (ReferenceEquals(compilingSpriteDocument, previous)) spriteSheetCancellation?.Cancel();
    }
    private void DrawSpriteSheetPreview()
    {
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        using var previewWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview);
        if (!previewWindow.DrawContents) return;
        var preview = spriteSheetPreview;
        if (preview == null) return;
        if (preview.RenderError is { } error && !ReferenceEquals(reportedSpriteSheetPreviewError, error))
        { reportedSpriteSheetPreviewError = error; RecordAssetPreviewRenderFailure("Sprite sheet preview", error); }
        var rowHeight = Math.Max(Gui.GetFrameHeight(), Math.Max(UiScale(20) + Gui.GetStyle().FramePadding.Y * 2, Gui.GetFontSize() + UiScale(8)));
        var scaleText = $"{preview.Scale:P}";
        var scaleWidth = Gui.CalcTextSize(scaleText).X;
        var frameText = $"{EditorLocalization.Text("Sprite:")} {Math.Min(preview.FrameCount, preview.CurrentFrame + 1)}/{preview.FrameCount}";
        var frameWidth = rowHeight * 2 + Gui.CalcTextSize(frameText).X + UiScale(8);
        var zoomWidth = 4 * (UiScale(16) + Gui.GetStyle().FramePadding.X * 2 + UiScale(4));
        var commandsWidth = Math.Max(1, Gui.GetContentRegionAvail().X - scaleWidth - UiScale(18));
        var wrapFrames = zoomWidth + UiScale(146) + frameWidth > commandsWidth;
        var toolbarHeight = Math.Max(rowHeight + UiScale(8), Gui.GetFontSize() + UiScale(10))
            + (wrapFrames ? rowHeight + UiScale(4) : 0);
        using (ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, NVector2.Zero))
        using (ImGuiScopes.Child("##SpritePreviewToolbar", new NVector2(0, toolbarHeight), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
        var toolbarOrigin = Gui.GetCursorScreenPos();
        var toolbarSize = Gui.GetContentRegionAvail();
        var controlsWidth = Math.Max(1, toolbarSize.X - scaleWidth - UiScale(10));
        using (ImGuiScopes.Child("##SpritePreviewCommands", new NVector2(controlsWidth, toolbarHeight), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
        Gui.SetCursorScreenPos(Gui.GetCursorScreenPos() + new NVector2(UiScale(4)));
        var rowY = Gui.GetCursorScreenPos().Y;
        if (DrawPreviewToolbarIcon("zoom-out", rowY, rowHeight)) preview.Zoom(false);
        if (DrawPreviewToolbarIcon("zoom-in", rowY, rowHeight)) preview.Zoom(true);
        if (DrawPreviewToolbarIcon("actual-size", rowY, rowHeight)) preview.ActualSize();
        if (DrawPreviewToolbarIcon("fit", rowY, rowHeight)) preview.Fit();
        DrawPreviewToolbarSeparator(rowY, rowHeight);
        PlacePreviewToolbarItem(rowY, rowHeight, Gui.GetFrameHeight());
        Gui.SetNextItemWidth(UiScale(120));
        var modeOpen = Gui.BeginCombo("##SpriteSheetMode", SpriteSheetPreviewModes[preview.SpriteTextures ? 1 : 0]);
        if (Controls.Enabled) Controls.Record("sprite-preview/mode");
        if (modeOpen)
        {
            try
            {
            var beforeModeFrame = preview.CurrentFrame; var beforeTextures = preview.SpriteTextures;
            var sprites = Gui.Selectable(SpriteSheetPreviewModes[0], !preview.SpriteTextures);
            if (sprites) preview.SpriteTextures = false;
            if (Controls.Enabled) { Controls.Record("sprite-preview/mode/sprites"); RecordSpritePreviewControl("mode/sprites", sprites, preview, beforeModeFrame, beforeTextures); }
            beforeModeFrame = preview.CurrentFrame; beforeTextures = preview.SpriteTextures;
            var textures = Gui.Selectable(SpriteSheetPreviewModes[1], preview.SpriteTextures);
            if (textures) preview.SpriteTextures = true;
            if (Controls.Enabled) { Controls.Record("sprite-preview/mode/textures"); RecordSpritePreviewControl("mode/textures", textures, preview, beforeModeFrame, beforeTextures); }
            }
            finally { Gui.EndCombo(); }
        }
        Gui.SameLine(0, 0);
        DrawPreviewToolbarSeparator(rowY, rowHeight);
        if (wrapFrames)
        {
            rowY += rowHeight + UiScale(4);
            Gui.SetCursorScreenPos(new NVector2(toolbarOrigin.X + UiScale(4), rowY));
        }
        using (ImGuiScopes.Disabled(preview.FrameCount == 0))
        {
        using (ImGuiScopes.Style(ImGuiStyleVar.FramePadding, new NVector2(UiScale(4), 0)))
        {
        PlacePreviewToolbarItem(rowY, rowHeight, rowHeight);
        var previousBefore = preview.CurrentFrame;
        var previousClicked = Gui.Button("<##SpritePrevious", new NVector2(0, rowHeight));
        if (previousClicked) preview.PreviousFrame();
        if (Controls.Enabled) RecordSpritePreviewControl("previous", previousClicked, preview, previousBefore, preview.SpriteTextures);
        Controls.Record("sprite-preview/previous");
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Previous sprite", "ToolTip"));
        Gui.SameLine(0, UiScale(4));
        PlacePreviewToolbarItem(rowY, rowHeight, Gui.GetFontSize());
        Gui.TextUnformatted(frameText);
        Gui.SameLine(0, UiScale(4));
        PlacePreviewToolbarItem(rowY, rowHeight, rowHeight);
        if (Gui.Button(">##SpriteNext", new NVector2(0, rowHeight))) preview.NextFrame();
        Controls.Record("sprite-preview/next");
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Next sprite", "ToolTip"));
        }
        }
        }
        Gui.GetWindowDrawList().AddText(new NVector2(toolbarOrigin.X + Math.Max(0, toolbarSize.X - scaleWidth - UiScale(5)),
            toolbarOrigin.Y + Math.Max(0, (rowHeight - Gui.GetFontSize()) / 2)), Gui.GetColorU32(ImGuiCol.Text), scaleText);
        }
        var size = Gui.GetContentRegionAvail(); size.Y -= UiScale(26) + Gui.GetStyle().ItemSpacing.Y;
        if (size.X > 0 && size.Y > 0)
        {
            try { preview.Resize((int)size.X, (int)size.Y); }
            catch (Exception failure)
            {
                if (!ReferenceEquals(reportedSpriteSheetPreviewError, failure))
                { reportedSpriteSheetPreviewError = failure; RecordAssetPreviewRenderFailure("Sprite sheet preview", failure); }
                return;
            }
            preview.Visible = true;
            var origin = Gui.GetCursorScreenPos();
            if (preview.RenderError == null) Gui.Image(preview.TextureRef, size);
            Gui.SetCursorScreenPos(origin);
            EditorPreviewPointerInput.Draw("##SpriteSheetInput", size);
            if (Gui.IsItemHovered() && Gui.GetIO().MouseWheel != 0)
            {
                var mouse = Gui.GetIO().MousePos;
                preview.Zoom(Gui.GetIO().MouseWheel > 0, new SVector2((mouse.X - origin.X) * preview.TargetWidth / size.X, (mouse.Y - origin.Y) * preview.TargetHeight / size.Y));
            }
            if (EditorPreviewPointerInput.TryPanDelta(out var delta))
            { preview.Pan(new SVector2(delta.X * preview.TargetWidth / size.X, delta.Y * preview.TargetHeight / size.Y)); }
        }
        using var framesDisabled = ImGuiScopes.Disabled(preview.FrameCount == 0);
        var footerOrigin = Gui.GetCursorScreenPos();
        var footerWidth = Gui.GetContentRegionAvail().X;
        Gui.SetCursorScreenPos(footerOrigin + new NVector2(UiScale(5)));
        Gui.SetNextItemWidth(Math.Max(1, footerWidth - UiScale(10)));
        var frame = preview.CurrentFrame + 1;
        var frameBefore = preview.CurrentFrame;
        var frameChanged = PreviewSliderControl.DrawInt(preview, "##SpriteFrame", ref frame, 1, Math.Max(1, preview.FrameCount));
        if (frameChanged) preview.CurrentFrame = frame - 1;
        if (Controls.Enabled) { Controls.Record("sprite-preview/frame"); RecordSpritePreviewControl("frame", frameChanged, preview, frameBefore, preview.SpriteTextures); }
        PreviewSliderControl.DrawTicks(1, preview.FrameCount, 1, true);
        Gui.SetCursorScreenPos(footerOrigin + new NVector2(0, UiScale(26)));
        Gui.Dummy(NVector2.Zero);
    }
}
