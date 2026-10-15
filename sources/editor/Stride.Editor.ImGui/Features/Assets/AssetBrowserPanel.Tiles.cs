using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserPanel
{
    private void ZoomAssetThumbnails(int direction)
    {
        var size = assetTileView ? assetTileThumbnailSize : assetGridThumbnailSize;
        var next = AssetBrowserZoomGesture.NextSize(size, direction);
        if (next == size) return;
        if (assetTileView) assetTileThumbnailSize = next;
        else assetGridThumbnailSize = next;
        host.SaveViewPreferences(true);
    }

    private void DrawAssetOptions()
    {
        if (!Gui.BeginPopup("Asset view options")) return;
        var displayOpen = host.BeginMenu(EditorLocalization.Widget("Display", "Menu"));
        if (host.CaptureControls) host.RecordControl("asset/options/display");
        if (displayOpen)
        {
            if (host.DrawMenuItem(EditorLocalization.Widget("Assets in selected folder only", "Menu"), "", assetBrowser.DisplayMode == AssetBrowserDisplayMode.AssetsOnly)) { assetBrowser.DisplayMode = AssetBrowserDisplayMode.AssetsOnly; host.SaveCollectionPreferences(); }
            if (host.DrawMenuItem(EditorLocalization.Widget("Assets in selected folder and subfolders", "Menu"), "", assetBrowser.DisplayMode == AssetBrowserDisplayMode.RecursiveAssets)) { assetBrowser.DisplayMode = AssetBrowserDisplayMode.RecursiveAssets; host.SaveCollectionPreferences(); }
            if (host.CaptureControls) host.RecordControl("asset/options/recursive");
            if (host.DrawMenuItem(EditorLocalization.Widget("Assets and folders in selected folder", "Menu"), "", assetBrowser.DisplayMode == AssetBrowserDisplayMode.AssetsAndFolders)) { assetBrowser.DisplayMode = AssetBrowserDisplayMode.AssetsAndFolders; host.SaveCollectionPreferences(); }
            Gui.EndMenu();
        }
        if (host.BeginMenu("Sort by"))
        {
            if (host.DrawMenuItem(EditorLocalization.Widget("Name", "Menu"), "", assetBrowser.Sort == AssetBrowserSort.Name)) { assetBrowser.Sort = AssetBrowserSort.Name; host.SaveCollectionPreferences(); }
            if (host.DrawMenuItem(EditorLocalization.Widget("Type", "Menu"), "", assetBrowser.Sort == AssetBrowserSort.TypeThenName)) { assetBrowser.Sort = AssetBrowserSort.TypeThenName; host.SaveCollectionPreferences(); }
            if (host.DrawMenuItem(EditorLocalization.Widget("Unsaved changes", "Menu"), "", assetBrowser.Sort == AssetBrowserSort.DirtyThenName)) { assetBrowser.Sort = AssetBrowserSort.DirtyThenName; host.SaveCollectionPreferences(); }
            if (host.DrawMenuItem(EditorLocalization.Widget("Date modified", "Menu"), "", assetBrowser.Sort == AssetBrowserSort.ModificationDateThenName)) { assetBrowser.Sort = AssetBrowserSort.ModificationDateThenName; host.SaveCollectionPreferences(); }
            Gui.EndMenu();
        }
        if (host.BeginMenu("View"))
        {
            if (host.DrawMenuItem(EditorLocalization.Widget("Tiles", "Menu"), "", assetTileView)) { assetTileView = true; host.SaveViewPreferences(); }
            if (host.DrawMenuItem("List", "", !assetTileView)) { assetTileView = false; host.SaveViewPreferences(); }
            var size = assetTileView ? assetTileThumbnailSize : assetGridThumbnailSize;
            Gui.BeginDisabled(size >= 128);
            if (host.Icons.Button("zoom-in", EditorLocalization.Text("Zoom in", "ToolTip"), 16)) ZoomAssetThumbnails(1);
            Gui.EndDisabled(); Gui.SameLine();
            Gui.BeginDisabled(size <= 16);
            if (host.Icons.Button("zoom-out", EditorLocalization.Text("Zoom out", "ToolTip"), 16)) ZoomAssetThumbnails(-1);
            Gui.EndDisabled(); Gui.EndMenu();
        }
        Gui.EndPopup();
    }

    private void DrawTiles(ref AssetEntry? clicked, ref AssetBrowserContent? clickedContent, ref bool toggle, ref bool range,
        ref bool openSelection, ref Action? assetAction)
    {
        if (!Gui.BeginChild("Asset tiles", Gui.GetContentRegionAvail())) { ImGuiScopes.EndChild(); return; }
        var style = Gui.GetStyle();
        var dpi = style.FontScaleDpi;
        var thumbnailSize = assetTileThumbnailSize * dpi;
        var tileWidth = Math.Max(thumbnailSize, 48 * dpi) + 18 * dpi;
        var nameHeight = TileNameHeight(); var typeHeight = TileTypeHeight();
        var tileHeight = thumbnailSize + nameHeight + typeHeight + 14 * dpi;
        var minGap = 4 * dpi;
        var rowHeight = tileHeight + minGap;
        var entries = assetBrowser.VisibleContent;
        var availableWidth = Gui.GetContentRegionAvail().X;
        var columns = Math.Max(1, Math.Min(entries.Count, (int)Math.Ceiling(availableWidth - minGap) / Math.Max(1, (int)(tileWidth + minGap))));
        var gap = columns <= 1 ? minGap : Math.Clamp((availableWidth - columns * tileWidth - 2 * minGap) / (columns - 1), minGap, Math.Max(minGap, thumbnailSize * 0.375f));
        var count = (entries.Count + columns - 1) / columns;
        var currentIndex = -1;
        for (var index = 0; index < entries.Count; index++)
            if (assetBrowser.IsContentSelected(entries[index])) { currentIndex = index; break; }
        var keyboardTarget = AssetTileKeyboardNavigation.ReadTarget(currentIndex, entries.Count, columns,
            (int)(Gui.GetContentRegionAvail().Y / (tileHeight + minGap * 2)));
        if (assetTextSearch.ReadTarget(entries, static row => row.Id, static row => row.Name) is { } textTarget)
            keyboardTarget = textTarget;
        if (keyboardTarget is { } target)
        {
            clickedContent = entries[target]; clicked = entries[target].Asset;
            toggle = range = openSelection = false;
            assetBrowser.CompleteReveal();
        }
        var left = Gui.GetCursorScreenPos().X + minGap;
        // Padding is actual layout, including an empty/fully clipped asset list.
        // Moving the cursor alone leaves ImGui's content bounds unregistered and
        // asserts at EndChild when the clipper submits no row.
        Gui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new NVector2(style.ItemSpacing.X, 0));
        try { Gui.Dummy(new NVector2(0, minGap)); }
        finally { Gui.PopStyleVar(); }
        var clipper = Gui.ImGuiListClipper();
        try
        {
            Gui.Begin(clipper, count, rowHeight);
            if (keyboardTarget is { } targetIndex) Gui.IncludeItemByIndex(clipper, targetIndex / columns);
            if (assetBrowser.RevealTarget is { } reveal)
                for (var index = 0; index < entries.Count; index++)
                    if (entries[index].Asset?.Id == reveal) { Gui.IncludeItemByIndex(clipper, index / columns); break; }
            while (Gui.Step(clipper))
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                var top = Gui.GetCursorScreenPos().Y;
                for (var column = 0; column < columns && row * columns + column < entries.Count; column++)
                {
                    var content = entries[row * columns + column];
                    var origin = new NVector2(left + column * (tileWidth + gap), top);
                    if (keyboardTarget == row * columns + column) Gui.SetKeyboardFocusHere();
                    if (content.Folder is { } folder)
                    {
                        DrawFolderTile(content, folder, origin, tileWidth, tileHeight, thumbnailSize, ref clickedContent, ref toggle, ref range, ref openSelection, ref assetAction);
                        if (keyboardTarget == row * columns + column) Gui.SetScrollHereY(0.5f);
                        continue;
                    }
                    var asset = content.Asset!;
                    Gui.SetCursorScreenPos(origin); Gui.PushID(asset.UiId);
                    var drawList = Gui.GetWindowDrawList();
                    drawList.AddRectFilled(origin + new NVector2(1, 0) * dpi, origin + new NVector2(tileWidth - dpi, tileHeight), Gui.GetColorU32(ImGuiCol.FrameBg));
                    var editingName = host.IsRenaming(asset);
                    if (editingName) Gui.Dummy(new NVector2(tileWidth, tileHeight));
                    else if (Gui.Selectable("##AssetTile", assetBrowser.IsSelected(asset.Id), ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap,
                        new NVector2(tileWidth, tileHeight)))
                    { clicked = asset; clickedContent = content; toggle = Gui.GetIO().KeyCtrl; range = Gui.GetIO().KeyShift; openSelection = Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left); }
                    var hovered = Gui.IsItemHovered();
                    if (keyboardTarget == row * columns + column) Gui.SetScrollHereY(0.5f);
                    if (assetBrowser.RevealTarget == asset.Id) { Gui.SetScrollHereY(0.5f); assetBrowser.CompleteReveal(); }
                    if (host.CaptureControls && Gui.IsItemVisible()) host.RecordControl("asset/" + asset.Name);
                    if (!editingName) host.DrawDragSource(asset);
                    if (Gui.BeginPopupContextItem("Asset actions"))
                    {
                        if (!assetBrowser.IsSelected(asset.Id)) { clicked = asset; clickedContent = content; toggle = range = false; openSelection = false; }
                        assetAction = host.DrawActions(asset) ?? assetAction;
                        Gui.EndPopup();
                    }
                    if (hovered && !Gui.IsPopupOpen("Asset actions"))
                    host.DrawTooltip(asset);

                    var thumbnailOrigin = origin + new NVector2((tileWidth - thumbnailSize) * 0.5f, 7 * dpi);
                    if (Gui.IsRectVisible(thumbnailOrigin, thumbnailOrigin + new NVector2(thumbnailSize)))
                        host.DrawThumbnail(asset, thumbnailOrigin, thumbnailSize);
                    drawList.AddRect(thumbnailOrigin - new NVector2(2 * dpi), thumbnailOrigin + new NVector2(thumbnailSize + 2 * dpi), Gui.GetColorU32(ImGuiCol.Border), 3 * dpi, ImDrawFlags.None, 2 * dpi);
                    if (!editingName && host.DrawTileBadges(asset, thumbnailOrigin, thumbnailSize) is { } badgeAction)
                    {
                        assetAction = badgeAction;
                        if (clicked?.Id == asset.Id) { clicked = null; clickedContent = null; openSelection = false; }
                    }
                    var textOrigin = origin + new NVector2(4 * dpi, thumbnailSize + 13 * dpi);
                    var textWidth = tileWidth - 8 * dpi;
                    drawList.PushClipRect(textOrigin, textOrigin + new NVector2(textWidth, nameHeight), true);
                    if (editingName)
                    {
                        Gui.SetCursorScreenPos(textOrigin);
                        host.DrawRenameField(asset, textWidth);
                    }
                    else DrawAssetTileName(asset, textOrigin, textWidth);
                    drawList.PopClipRect();
                    textOrigin.Y += nameHeight;
                    drawList.PushClipRect(textOrigin, textOrigin + new NVector2(textWidth, typeHeight), true);
                    using (ImGuiScopes.Font(10 * dpi))
                    {
                        var type = EllipsizeNativeTileLabel(asset.TypeLabel, textWidth);
                        PaintCenteredTileLabel(type, textOrigin, textWidth, typeHeight, Gui.GetColorU32(ImGuiCol.TextDisabled));
                    }
                    drawList.PopClipRect(); Gui.PopID();
                }
                Gui.SetCursorScreenPos(new NVector2(left, top));
                Gui.Dummy(new NVector2(columns * (tileWidth + gap) - gap, rowHeight - style.ItemSpacing.Y));
            }
            DrawAssetBackgroundContext(ref assetAction);
        }
        finally { Gui.Destroy(clipper); ImGuiScopes.EndChild(); }
    }

}
