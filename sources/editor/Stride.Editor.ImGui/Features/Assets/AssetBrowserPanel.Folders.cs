using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserPanel
{
    private void FolderInput(AssetBrowserContent content, AssetBrowserFolder folder, ref AssetBrowserContent? clicked,
        ref bool toggle, ref bool range, ref bool open, ref Action? action)
    {
        var hovered = Gui.IsItemHovered();
        if (Gui.IsItemClicked(ImGuiMouseButton.Left))
        { clicked = content; toggle = Gui.GetIO().KeyCtrl; range = Gui.GetIO().KeyShift; open = Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left); }
        host.DrawFolderDragSource(folder);
        host.DrawFolderDropTarget(folder);
        if (Gui.BeginPopupContextItem("Folder actions"))
        {
            if (!assetBrowser.IsContentSelected(content)) { clicked = content; toggle = range = open = false; }
            action = host.DrawFolderActions(folder) ?? action;
            Gui.EndPopup();
        }
        if (hovered && !Gui.IsPopupOpen("Folder actions")) host.DrawFolderTooltip(folder);
        if (host.CaptureControls && Gui.IsItemVisible()) host.RecordControl("asset-folder/" + folder.Id);
    }
    private void DrawFolderTile(AssetBrowserContent content, AssetBrowserFolder folder, NVector2 origin, float width, float height,
        float thumbnail, ref AssetBrowserContent? clicked, ref bool toggle, ref bool range, ref bool open, ref Action? action)
    {
        var dpi = Gui.GetStyle().FontScaleDpi;
        var nameHeight = TileNameHeight(); var typeHeight = TileTypeHeight();
        Gui.SetCursorScreenPos(origin); Gui.PushID(content.Id);
        var draw = Gui.GetWindowDrawList();
        draw.AddRectFilled(origin + new NVector2(1, 0) * dpi, origin + new NVector2(width - dpi, height), Gui.GetColorU32(ImGuiCol.FrameBg));
        var renaming = host.IsRenamingFolder(folder);
        if (renaming) Gui.Dummy(new NVector2(width, height));
        else
        {
            Gui.Selectable("##Folder", assetBrowser.IsContentSelected(content), ImGuiSelectableFlags.AllowDoubleClick, new NVector2(width, height));
            FolderInput(content, folder, ref clicked, ref toggle, ref range, ref open, ref action);
        }
        var icon = origin + new NVector2((width - thumbnail) * .5f, 7 * dpi);
        draw.AddImage(host.Icons.Get("folder"), icon, icon + new NVector2(thumbnail));
        var label = origin + new NVector2(4 * dpi, thumbnail + 13 * dpi); var labelWidth = width - 8 * dpi;
        draw.PushClipRect(label, label + new NVector2(labelWidth, nameHeight), true);
        if (renaming) { Gui.SetCursorScreenPos(label); host.DrawFolderRenameField(folder, labelWidth); }
        else using (ImGuiScopes.Font(12 * dpi))
        {
            var text = EllipsizeNativeTileLabel(folder.Name, labelWidth);
            PaintCenteredTileLabel(text, label, labelWidth, nameHeight, Gui.GetColorU32(ImGuiCol.Text));
        }
        draw.PopClipRect(); label.Y += nameHeight;
        using (ImGuiScopes.Font(10 * dpi))
            PaintCenteredTileLabel("Folder", label, labelWidth, typeHeight, Gui.GetColorU32(ImGuiCol.TextDisabled));
        Gui.PopID();
    }
    private void DrawFolderTableRow(AssetBrowserContent content, AssetBrowserFolder folder, ref AssetBrowserContent? clicked,
        ref bool toggle, ref bool range, ref bool open, ref Action? action)
    {
        var thumbnail = assetGridThumbnailSize * Gui.GetStyle().FontScaleDpi;
        var height = BrowserTableRowHeight();
        Gui.TableNextRow(ImGuiTableRowFlags.None, height); Gui.TableNextColumn(); Gui.PushID(content.Id);
        var origin = Gui.GetCursorScreenPos();
        var width = Gui.GetContentRegionAvail().X;
        var renaming = host.IsRenamingFolder(folder);
        if (renaming) Gui.Dummy(new NVector2(width, height));
        else
        {
            Gui.Selectable("##Folder", assetBrowser.IsContentSelected(content), ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick, new NVector2(0, height));
            FolderInput(content, folder, ref clicked, ref toggle, ref range, ref open, ref action);
        }
        var draw = Gui.GetWindowDrawList();
        draw.AddImage(host.Icons.Get("folder"), origin, origin + new NVector2(thumbnail));
        var text = origin + new NVector2(thumbnail + Gui.GetStyle().ItemSpacing.X, Math.Max(0, (height - host.MeasureLabel(folder.Name).Y) * .5f));
        draw.PushClipRect(text, new NVector2(origin.X + width, origin.Y + height), true);
        if (renaming) { Gui.SetCursorScreenPos(text); host.DrawFolderRenameField(folder, Math.Max(1, width - thumbnail - Gui.GetStyle().ItemSpacing.X)); }
        else host.PaintLabel(folder.Name, text, text, new(origin.X + width, origin.Y + height), Gui.GetColorU32(ImGuiCol.Text)); draw.PopClipRect();
        Gui.TableNextColumn();
        Gui.PushStyleColor(ImGuiCol.Text, Gui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        host.DrawLabel("Folder"); Gui.PopStyleColor(); Gui.PopID();
    }
}
