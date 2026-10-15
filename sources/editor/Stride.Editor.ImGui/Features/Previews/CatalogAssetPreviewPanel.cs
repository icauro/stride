using Hexa.NET.ImGui;
using Stride.Games;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed class CatalogAssetPreviewPanel(ICatalogAssetPreviewHost host)
{
    private EntityAssetPreview? entityPreview;
    private HeightmapAssetPreview? heightmap;
    private bool sceneNotice;
    private Exception? reportedError;
    private string kind = "";
    internal bool UsesRuntimeTexture(Stride.Graphics.Texture texture) => entityPreview?.UsesRuntimeTexture(texture) == true;
    internal (string? Url, bool Ready) CaptureOwner => entityPreview is { } entity ? (entity.Name, entity.CaptureReady)
        : heightmap is { } map ? (map.SourceUrl, map.CaptureReady) : (null, false);
    public bool HasContent => entityPreview != null || heightmap != null || sceneNotice;
    public void SetEntity(EntityAssetPreview next, string previewKind) { Close(); entityPreview = next; kind = previewKind; }
    public void SetHeightmap(HeightmapAssetPreview next) { Close(); heightmap = next; kind = "heightmap-preview"; }
    public void ShowSceneNotice() { Close(); sceneNotice = true; }
    public Action CaptureViewRestore()
    {
        var camera = entityPreview?.Camera.CaptureState();
        var previousHeightmap = heightmap;
        return () =>
        {
            if (camera is { } view && entityPreview != null) entityPreview.Camera.RestoreState(view);
            if (previousHeightmap != null && heightmap != null) heightmap.CopyViewFrom(previousHeightmap);
        };
    }
    public void ResetVisibility()
    {
        if (entityPreview != null) entityPreview.Visible = false;
        if (heightmap != null) heightmap.Visible = false;
    }
    public void Render(GameTime time) { entityPreview?.Render(time); heightmap?.Render(); }
    public void Close(bool immediate = false)
    {
        if (entityPreview is { } entity) { entity.Visible = false; entityPreview = null; host.ReleasePreview(entity, immediate); }
        if (heightmap is { } map) { map.Visible = false; heightmap = null; host.ReleasePreview(map, immediate); }
        sceneNotice = false; reportedError = null;
    }
    public bool Draw(ref bool visible, ref bool focus)
    {
        if (entityPreview == null && heightmap == null && !sceneNotice) return false;
        if (focus) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focus = false; }
        using var window = ImGuiScopes.Window(PreviewWindowCaption.Text, ref visible);
        if (!window.DrawContents) return true;
        if (sceneNotice)
        {
            var text = EditorLocalization.Text("Scenes can't be previewed. To see the scene, open it in the scene editor.");
            var region = Gui.GetContentRegionAvail(); var size = Gui.CalcTextSize(text);
            Gui.SetCursorPos(Gui.GetCursorPos() + new Vector2(Math.Max(0, (region.X - size.X) / 2), Math.Max(0, (region.Y - size.Y) / 2)));
            Gui.TextWrapped(text);
        }
        else if (entityPreview is { } entity) DrawCatalogEntityPreview(entity);
        else if (heightmap is { } map) DrawHeightmapPreview(map);
        return true;
    }

    private void DrawCatalogEntityPreview(EntityAssetPreview preview)
    {
        if (PreviewModelToolbar.Draw(host.Icons, host.Controls, kind + "/reset")) preview.ResetCamera();
        var size = Gui.GetContentRegionAvail();
        if (size.X <= 0 || size.Y <= 0) return;
        try { preview.Resize((int)size.X, (int)size.Y); }
        catch (Exception error) { ReportCatalogPreviewFailure(error); return; }
        preview.Visible = true;
        if (preview.RenderError is { } failure) { ReportCatalogPreviewFailure(failure); host.DrawSelectableText("##EntityPreviewError", failure.Message); }
        var origin = Gui.GetCursorScreenPos();
        if (preview.RenderError == null) Gui.Image(preview.TextureRef, size);
        Gui.SetCursorScreenPos(origin); host.DrawCameraInput("##CatalogEntityInput", size, preview.Camera);
        host.RegisterCaptureItem(kind + "/viewport", "viewport", "Asset preview");
        if (host.Controls.Enabled) host.Controls.Record(kind + "/image");
    }

    private void DrawHeightmapPreview(HeightmapAssetPreview preview)
    {
        var toolbarHeight = 20 * Gui.GetStyle().FontScaleDpi + Gui.GetStyle().FramePadding.Y * 2;
        using (ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (ImGuiScopes.Child("##HeightmapPreviewToolbar", new Vector2(0, toolbarHeight), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
        var rowY = Gui.GetCursorScreenPos().Y;
        if (host.DrawToolbarIcon("zoom-out", rowY, toolbarHeight)) preview.Zoom(false);
        if (host.Controls.Enabled) host.Controls.Record("heightmap-preview/zoom-out");
        if (host.DrawToolbarIcon("zoom-in", rowY, toolbarHeight)) preview.Zoom(true);
        if (host.Controls.Enabled) host.Controls.Record("heightmap-preview/zoom-in");
        if (host.DrawToolbarIcon("actual-size", rowY, toolbarHeight)) preview.ActualSize();
        if (host.Controls.Enabled) host.Controls.Record("heightmap-preview/actual-size");
        if (host.DrawToolbarIcon("fit", rowY, toolbarHeight)) preview.Fit();
        if (host.Controls.Enabled) host.Controls.Record("heightmap-preview/fit");
        }
        var available = Gui.GetContentRegionAvail(); var footer = PreviewImageStatusBar.Height;
        var footerOrigin = Gui.GetCursorScreenPos() + new Vector2(0, Math.Max(0, available.Y - footer));
        var size = new Vector2(available.X, available.Y - footer);
        if (size.X <= 0 || size.Y <= 0)
        {
            Gui.SetCursorScreenPos(footerOrigin); PreviewImageStatusBar.Draw(preview.Scale, preview.Width, preview.Length, true); return;
        }
        try { preview.Resize((int)size.X, (int)size.Y); }
        catch (Exception error)
        {
            ReportCatalogPreviewFailure(error);
            Gui.SetCursorScreenPos(footerOrigin); PreviewImageStatusBar.Draw(preview.Scale, preview.Width, preview.Length, true); return;
        }
        preview.Visible = true;
        if (preview.RenderError is { } failure) ReportCatalogPreviewFailure(failure);
        var origin = Gui.GetCursorScreenPos();
        if (preview.RenderError == null) Gui.Image(preview.TextureRef, size);
        Gui.SetCursorScreenPos(origin); EditorPreviewPointerInput.Draw("##HeightmapInput", size);
        host.RegisterCaptureItem("heightmap-preview/viewport", "viewport", "Heightmap preview");
        if (host.Controls.Enabled) host.Controls.Record("heightmap-preview/image");
        var io = Gui.GetIO();
        if (Gui.IsItemHovered() && io.MouseWheel != 0)
            preview.Zoom(io.MouseWheel > 0, new SVector2((io.MousePos.X - origin.X) * preview.TargetWidth / size.X,
                (io.MousePos.Y - origin.Y) * preview.TargetHeight / size.Y));
        if (EditorPreviewPointerInput.TryPanDelta(out var delta))
            preview.Pan(new SVector2(delta.X * preview.TargetWidth / size.X, delta.Y * preview.TargetHeight / size.Y));
        Gui.SetCursorScreenPos(footerOrigin); PreviewImageStatusBar.Draw(preview.Scale, preview.Width, preview.Length, true);
    }

    private void ReportCatalogPreviewFailure(Exception error)
    {
        if (ReferenceEquals(error, reportedError)) return;
        reportedError = error; host.ReportFailure(kind, error);
    }
}
