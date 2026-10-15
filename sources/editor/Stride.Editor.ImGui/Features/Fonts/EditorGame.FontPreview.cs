using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SpriteFontAssetPreview? fontPreview;
    private AssetMetadataDocument? requestedFontDocument, fontPreviewDocument;
    private Exception? reportedFontPreviewError;
    private string fontPreviewText = "";
    private void OpenFontPreview(AssetEntry entry)
    {
        if (projectContent == null)
        {
            GetAssetTagDocument(entry); // Keep real font settings available in the CPU authoring session.
            ClearSelectedAssetPreview(); return;
        }
        CloseCatalogAssetPreviews();
        CloseAnimatedMediaPreviews();
        CloseSkyboxPreview(); CloseSpriteSheetPreview(); CloseFontPreview();
        texturePreview?.Dispose(); texturePreview = null; modelPreview?.Dispose(); modelPreview = null; materialPreview?.Dispose(); materialPreview = null;
        var owner = GetAssetTagDocument(entry) as AssetMetadataDocument; requestedFontDocument = fontPreviewDocument = owner;
        var item = workspace!.FindAsset(entry.Id); selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true; ResetFontPreviewText(true);
        if (Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(item.Location.ToString()))
        {
            fontPreview = CreateAssetPreview(entry, () => CreateFontPreview(entry));
            if (fontPreview != null) requestedFontDocument = null;
        }
    }
    private SpriteFontAssetPreview CreateFontPreview(AssetEntry entry)
    {
        var item = workspace!.FindAsset(entry.Id); var url = item.Location.ToString();
        var lease = AcquireCompiledFont(url); var content = lease?.Bundle ?? projectContent;
        if (content == null) { lease?.Dispose(); throw new InvalidOperationException("The project has not been compiled."); }
        var owner = documents.AuthoredDocuments.OfType<AssetMetadataDocument>().FirstOrDefault(document => document.AuthoredAsset.Id == entry.Id);
        try { return new SpriteFontAssetPreview(this, renderer!, content, url, owner?.AuthoredAsset ?? item.Asset,
            owner == null ? item.Package.GetGameSettingsAssetOrDefault().GetOrDefault<RenderingSettings>().ColorSpace : CurrentFontRevision(owner).ColorSpace, lease); }
        catch { lease?.Dispose(); throw; }
    }
    private void ReloadFontPreview(AssetMetadataDocument owner)
    {
        if (!string.Equals(assetPreviewPath, owner.Path, StringComparison.OrdinalIgnoreCase)) return;
        var entry = workspace!.Assets.FirstOrDefault(asset => asset.Id == owner.AuthoredAsset.Id); if (entry == null) return;
        Execute(() =>
        {
            var next = CreateFontPreview(entry); next.Sample = validatedFontPreviewText;
            if (fontPreview != null) { next.CopyViewFrom(fontPreview); RetireEditorPreview(fontPreview); }
            fontPreview = next; fontPreviewDocument = owner; requestedFontDocument = null; reportedFontPreviewError = null;
        });
    }
    private void CloseFontPreview()
    {
        if (fontPreview != null) { fontPreview.Visible = false; RetireEditorPreview(fontPreview); fontPreview = null; }
        requestedFontDocument = fontPreviewDocument = null; reportedFontPreviewError = null;
        ResetFontPreviewText(false);
    }
    private void RebindFontSettingsDocument(IEditorDocument previous, IEditorDocument next)
    {
        if (ReferenceEquals(requestedFontDocument, previous) || ReferenceEquals(fontPreviewDocument, previous))
        {
            if (next is AssetMetadataDocument { IsFontAsset: true } owner)
            { requestedFontDocument = fontPreviewDocument = owner; assetPreviewPath = owner.Path; compiledFontRevisions.Remove(owner); }
            else CloseFontPreview();
        }
        if (ReferenceEquals(compilingFontDocument, previous)) fontCancellation?.Cancel();
    }
    private void DrawFontPreview()
    {
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        using var previewWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview);
        if (!previewWindow.DrawContents) return;
        var preview = fontPreview; if (preview == null) return;
        var textHeight = FontPreviewTextHeight(); var margin = UiScale(4);
        var buttonSize = new NVector2(UiScale(16)) + Gui.GetStyle().FramePadding * 2;
        var rowHeight = Math.Max(textHeight, buttonSize.Y);
        using (ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, NVector2.Zero))
        using (ImGuiScopes.Style(ImGuiStyleVar.WindowPadding, NVector2.Zero))
        using (ImGuiScopes.Child("##FontToolbar", new NVector2(0, rowHeight + margin * 2), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
        var contentOrigin = Gui.GetCursorScreenPos() + new NVector2(margin);
        var width = Math.Max(0, Gui.GetContentRegionAvail().X - margin * 2 - buttonSize.X);
        var textOrigin = contentOrigin + new NVector2(0, (rowHeight - textHeight) * .5f);
        Gui.SetCursorScreenPos(textOrigin);
        if (width > 0)
        {
            DrawFontPreviewText(preview, new NVector2(width, textHeight));
            RecordFontPreviewTextUi(preview);
        }
        else Gui.Dummy(new NVector2(0, textHeight));
        if (width > 0 && fontPreviewText.Length == 0 && !Gui.IsItemActive())
            Gui.GetWindowDrawList().AddText(textOrigin + Gui.GetStyle().FramePadding, Gui.GetColorU32(ImGuiCol.TextDisabled), EditorLocalization.Text("Preview text"));
        Gui.SetCursorScreenPos(contentOrigin + new NVector2(width, 0));
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding,
            new NVector2(Gui.GetStyle().FramePadding.X, Math.Max(0, (rowHeight - UiScale(16)) * .5f)));
        try
        {
            var clearPressed = icons!.Button("preview-clear-text", null, 16);
            RecordFontPreviewClearUi(preview, clearPressed);
            if (Controls.Enabled) Controls.Record("font-preview/clear");
            if (clearPressed)
            { fontPreviewText = ""; ValidateFontPreviewText(preview, ""); }
        }
        finally { Gui.PopStyleVar(); }
        }
        if (preview.RenderError is { } error && !ReferenceEquals(reportedFontPreviewError, error))
        { reportedFontPreviewError = error; RecordAssetPreviewRenderFailure("Font preview", error); }
        var size = Gui.GetContentRegionAvail();
        if (size.X > 0 && size.Y > 0)
        {
            try { preview.Resize((int)size.X, (int)size.Y); }
            catch (Exception resizeFailure)
            {
                if (!ReferenceEquals(reportedFontPreviewError, resizeFailure)) { reportedFontPreviewError = resizeFailure; RecordAssetPreviewRenderFailure("Font preview", resizeFailure); }
                return;
            }
            preview.Visible = true; var origin = Gui.GetCursorScreenPos();
            if (preview.RenderError == null) Gui.Image(preview.TextureRef, size);
            Gui.SetCursorScreenPos(origin); EditorPreviewPointerInput.Draw("##FontInput", size);
            if (Gui.IsItemHovered() && Gui.GetIO().MouseWheel != 0)
            {
                var io = Gui.GetIO();
                preview.NavigateWheel(io.MouseWheel > 0,
                    new SVector2((io.MousePos.X - origin.X) * preview.TargetWidth / size.X,
                        (io.MousePos.Y - origin.Y) * preview.TargetHeight / size.Y));
            }
            if (EditorPreviewPointerInput.TryPanDelta(out var delta))
            { preview.Pan(new SVector2(delta.X * preview.TargetWidth / size.X, delta.Y * preview.TargetHeight / size.Y)); }
        }
    }
}
