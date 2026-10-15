using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private readonly AssetBrowserTextSearch modalTextSearch = new();
    private int modalThumbnailSize = 96;
    private sealed record PickerTileLabel(string Name, float Width, float Font, float Dpi, string Locale, int FontIdentity, string[] Lines);
    private readonly Dictionary<string, PickerTileLabel> modalTileLabels = [];
    private readonly Dictionary<uint, string> modalTileItemIds = [];

    private void DrawPickerViewOptions()
    {
        var clicked = host.PickerIcons is { } icons ? icons.Button("scene-toolbar-view", "Asset view options", 16)
            : Gui.SmallButton(EditorLocalization.Widget("View", "Menu"));
        if (clicked) Gui.OpenPopup("##PickerViewOptions");
        if (!Gui.BeginPopup("##PickerViewOptions")) return;
        try
        {
            if (Gui.BeginMenu(EditorLocalization.Widget("Display", "Menu")))
            {
                foreach (var (label, mode) in new[]
                {
                    ("Assets in selected folder only", AssetBrowserDisplayMode.AssetsOnly),
                    ("Assets and folders in selected folder", AssetBrowserDisplayMode.AssetsAndFolders),
                    ("Assets in selected folder and subfolders", AssetBrowserDisplayMode.RecursiveAssets),
                }) if (Gui.MenuItem(EditorLocalization.Widget(label, "Menu"), "", modalBrowser.DisplayMode == mode)) modalBrowser.DisplayMode = mode;
                Gui.EndMenu();
            }
            if (Gui.BeginMenu(EditorLocalization.Widget("Sort by", "Menu")))
            {
                foreach (var (label, sort) in new[]
                {
                    ("Name", AssetBrowserSort.Name), ("Date modified", AssetBrowserSort.ModificationDateThenName),
                    ("Type", AssetBrowserSort.TypeThenName), ("Unsaved changes", AssetBrowserSort.DirtyThenName),
                }) if (Gui.MenuItem(EditorLocalization.Widget(label, "Menu"), "", modalBrowser.Sort == sort)) modalBrowser.Sort = sort;
                Gui.EndMenu();
            }
            if (Gui.BeginMenu(EditorLocalization.Widget("View", "Menu")))
            {
                Gui.MenuItem(EditorLocalization.Widget("Tiles", "Menu"), "", true);
                using (ImGuiScopes.Disabled(modalThumbnailSize >= 128)) if (Gui.MenuItem(EditorLocalization.Widget("Zoom in", "ToolTip"))) ZoomPickerThumbnails(1);
                using (ImGuiScopes.Disabled(modalThumbnailSize <= 16)) if (Gui.MenuItem(EditorLocalization.Widget("Zoom out", "ToolTip"))) ZoomPickerThumbnails(-1);
                Gui.EndMenu();
            }
        }
        finally { Gui.EndPopup(); }
    }
    private void ZoomPickerThumbnails(int direction)
    {
        var next = AssetBrowserZoomGesture.NextSize(modalThumbnailSize, direction);
        if (next != modalThumbnailSize) modalThumbnailSize = host.SavePickerThumbnailSize(next);
    }
    private bool DrawPickerTiles(ModalRequest request, IReadOnlyDictionary<Stride.Core.Assets.AssetId, ContentReferenceChoice> choices)
    {
        using var child = ImGuiScopes.Child("##PickerTiles", Gui.GetContentRegionAvail(), ImGuiChildFlags.None, ImGuiWindowFlags.None);
        if (!child.DrawContents) return false;
        var zoom = AssetBrowserZoomGesture.ReadDirection(true); if (zoom != 0) ZoomPickerThumbnails(zoom);
        host.SyncPickerSortMetadata(); modalBrowser.UpdateSortMetadata(host.PickerSortMetadata);
        host.RefreshPickerTileStatus();
        var rows = modalBrowser.VisibleContent.Where(row => row.Asset == null || choices.ContainsKey(row.Asset.Id)).ToArray();
        if (rows.Length == 0) { modalTileItemIds.Clear(); DrawPickerBackgroundContext(request); return false; }
        var dpi = Gui.GetStyle().FontScaleDpi; var thumbnail = modalThumbnailSize * dpi;
        var tileWidth = Math.Max(thumbnail, 48 * dpi) + 18 * dpi; var tileHeight = thumbnail + 64 * dpi; var minGap = 4 * dpi;
        var rowHeight = tileHeight + minGap;
        var available = Gui.GetContentRegionAvail();
        var columns = Math.Max(1, Math.Min(rows.Length, (int)(available.X / (tileWidth + minGap))));
        var gap = columns < 2 ? minGap : Math.Clamp((available.X - columns * tileWidth - 2 * minGap) / (columns - 1), minGap, Math.Max(minGap, thumbnail * .375f));
        var focusedIndex = modalTileItemIds.TryGetValue(Gui.GetCurrentContext().NavId, out var focusedTile)
            ? Array.FindIndex(rows, row => row.Id == focusedTile) : -1;
        var keyboardTarget = AssetTileKeyboardNavigation.ReadTarget(focusedIndex >= 0 ? focusedIndex : Array.FindIndex(rows, modalBrowser.IsContentSelected), rows.Length, columns,
            Math.Max(1, (int)(available.Y / (tileHeight + minGap))), true);
        var io = Gui.GetIO();
        var textTarget = request.IsCurrent() && !request.ContextMutationPending
            ? modalTextSearch.ReadTarget(rows, static row => row.Id, static row => row.Name, true) : null;
        if (textTarget != null) keyboardTarget = textTarget;
        AssetBrowserContent? selected = keyboardTarget is { } target && (textTarget != null || !io.KeyCtrl) ? rows[target] : null;
        var toggleSelection = false; var rangeSelection = textTarget == null && selected != null && io.KeyShift;
        var ownsSelectionKeys = request.IsCurrent() && !io.AppFocusLost && !io.KeyAlt && !io.KeySuper
            && !io.WantTextInput && !Gui.IsAnyItemActive() && Gui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows)
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        if (ownsSelectionKeys && focusedIndex >= 0 && Gui.IsKeyPressed(ImGuiKey.Space, false))
        {
            ImGuiP.SetKeyOwner(ImGuiKey.Space, Gui.GetID("PickerSelectFocused"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            selected = rows[focusedIndex]; toggleSelection = io.KeyCtrl; rangeSelection = io.KeyShift;
        }
        var selectAll = ownsSelectionKeys && io.KeyCtrl && !io.KeyShift && Gui.IsKeyPressed(ImGuiKey.A, false);
        if (selectAll) ImGuiP.SetKeyOwner(ImGuiKey.A, Gui.GetID("PickerSelectAll"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
        modalTileItemIds.Clear();
        var activate = false;
        Action? badgeCommand = null;
        PickerAuthoringCommand? transfer = null;
        var left = Gui.GetCursorScreenPos().X + minGap; var clipper = Gui.ImGuiListClipper();
        try
        {
            Gui.Begin(clipper, (rows.Length + columns - 1) / columns, rowHeight);
            if (keyboardTarget is { } keyboard) Gui.IncludeItemByIndex(clipper, keyboard / columns);
            if (focusedIndex >= 0) Gui.IncludeItemByIndex(clipper, focusedIndex / columns);
            if (modalBrowser.RevealTarget is { } reveal)
            { var index = Array.FindIndex(rows, row => row.Asset?.Id == reveal); if (index >= 0) Gui.IncludeItemByIndex(clipper, index / columns); }
            while (Gui.Step(clipper))
                for (var rowIndex = clipper.DisplayStart; rowIndex < clipper.DisplayEnd; rowIndex++)
                {
                    var top = Gui.GetCursorScreenPos().Y;
                    for (var column = 0; column < columns && rowIndex * columns + column < rows.Length; column++)
                    {
                        var index = rowIndex * columns + column; var row = rows[index]; var origin = new Vector2(left + column * (tileWidth + gap), top);
                        Gui.SetCursorScreenPos(origin); Gui.PushID(row.Id);
                        try
                        {
                            modalTileItemIds[Gui.GetID("##Tile")] = row.Id;
                            var draw = Gui.GetWindowDrawList();
                            draw.AddRectFilled(origin, origin + new Vector2(tileWidth, tileHeight), Gui.GetColorU32(ImGuiCol.FrameBg));
                            if (keyboardTarget == index) Gui.SetKeyboardFocusHere();
                            var renaming = renamingContent?.Id == row.Id;
                            if (renaming) Gui.Dummy(new Vector2(tileWidth, tileHeight));
                            else if (Gui.Selectable("##Tile", modalBrowser.IsContentSelected(row), ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap, new Vector2(tileWidth, tileHeight)))
                            { selected = row; toggleSelection = io.KeyCtrl; rangeSelection = io.KeyShift; activate = Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left); }
                            if (Gui.IsItemVisible() && row.Asset != null) RecordControl?.Invoke("template/reference/asset/" + row.Asset.Id);
                            var hovered = Gui.IsItemHovered();
                            DrawPickerContext(row, request, hovered);
                            if (!renaming && !request.ContextMutationPending)
                            {
                                host.DrawPickerDragSource(row, modalBrowser, request.IsCurrent);
                                if (row.Folder is { } destination) transfer = host.DrawPickerDropTarget(destination, request.IsCurrent) ?? transfer;
                            }
                            if (keyboardTarget == index || modalBrowser.RevealTarget is { } revealTarget && row.Asset?.Id == revealTarget)
                            { Gui.SetScrollHereY(.5f); if (row.Asset != null) modalBrowser.CompleteReveal(); }
                            var image = origin + new Vector2((tileWidth - thumbnail) * .5f, 7 * dpi);
                            if (row.Asset is { } asset) host.RenderThumbnail(asset, image, thumbnail);
                            else if (host.PickerIcons is { } icons) draw.AddImage(icons.Get("folder"), image, image + new Vector2(thumbnail));
                            var badges = row.Asset is { } badgeAsset ? host.DrawPickerTileBadges(badgeAsset, image, thumbnail, request.IsCurrent) : default;
                            if (badges.Command != null)
                            {
                                badgeCommand = badges.Command;
                                if (selected?.Id == row.Id && keyboardTarget != index) { selected = null; activate = false; }
                            }
                            var label = origin + new Vector2(4 * dpi, thumbnail + 13 * dpi);
                            if (renaming) { Gui.SetCursorScreenPos(label); DrawPickerRename(row, request, tileWidth - 8 * dpi); }
                            else DrawPickerTileLabel(row, label, tileWidth - 8 * dpi);
                            if (row.Asset is { } typed)
                                using (ImGuiScopes.Font(10 * dpi))
                                {
                                    var text = AssetBrowserPanel.EllipsizeTileLabel(typed.TypeLabel, tileWidth - 8 * dpi, value => host.MeasurePickerLabel(value).X);
                                    var textSize = host.MeasurePickerLabel(text);
                                    host.PaintPickerLabel(text, origin + new Vector2(Math.Max(0, (tileWidth - textSize.X) * .5f), tileHeight - textSize.Y - 3 * dpi),
                                        origin, origin + new Vector2(tileWidth, tileHeight), Gui.GetColorU32(ImGuiCol.TextDisabled));
                                }
                            if (hovered && !badges.Hovered && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) host.DrawPickerTooltip(row);
                        }
                        finally { Gui.PopID(); }
                    }
                    // Manual tile placement must publish a real row extent before the child ends.
                    // Merely advancing the cursor asserts in native ImGui when it extends the boundary.
                    Gui.SetCursorScreenPos(new Vector2(left, top));
                    Gui.Dummy(new Vector2(columns * (tileWidth + gap) - gap, Math.Max(0, rowHeight - Gui.GetStyle().ItemSpacing.Y)));
                }
        }
        finally { Gui.Destroy(clipper); }
        DrawPickerBackgroundContext(request);
        if (badgeCommand != null)
        {
            badgeCommand();
            if (host.ContextCommandPending) { contextSuspended = true; contextWaitFrames = 2; }
        }
        if (transfer != null) { RunPickerTransfer(request, transfer); return false; }
        if (selectAll && request.IsCurrent()) modalBrowser.SelectAllContent(rows);
        if (selected != null && request.IsCurrent())
        {
            modalBrowser.SelectContent(selected, toggleSelection, rangeSelection, rows);
            if (activate)
            {
                if (modalBrowser.SelectedContent.Count == 1 && selected.Folder is { } folder) modalBrowser.SelectFolder(folder);
                else if (modalBrowser.SelectedCount == 1 && TryAccept(request, choices)) return true;
            }
        }
        return false;
    }
    private void DrawPickerTileLabel(AssetBrowserContent row, Vector2 origin, float width)
    {
        using var font = ImGuiScopes.Font(12 * Gui.GetStyle().FontScaleDpi);
        var name = row.Name + (row.Asset is { } asset && host.PickerSortMetadata.Get(asset.Id).IsDirty ? "*" : "");
        var size = Gui.GetFontSize();
        var dpi = Gui.GetStyle().FontScaleDpi; var locale = System.Globalization.CultureInfo.CurrentUICulture.Name; var fontIdentity = Gui.GetFont().GetHashCode();
        if (!modalTileLabels.TryGetValue(row.Id, out var label) || label.Name != name || label.Width != width || label.Font != size
            || label.Dpi != dpi || label.Locale != locale || label.FontIdentity != fontIdentity)
            modalTileLabels[row.Id] = label = new(name, width, size, dpi, locale, fontIdentity, AssetBrowserPanel.WrapTileLabel(name, width, value => host.MeasurePickerLabel(value).X));
        var minimum = origin; var maximum = origin + new Vector2(width, 32 * dpi);
        foreach (var line in label.Lines)
        {
            var textSize = host.MeasurePickerLabel(line);
            host.PaintPickerLabel(line, origin + new Vector2(Math.Max(0, (width - textSize.X) * .5f), 0), minimum, maximum, Gui.GetColorU32(ImGuiCol.Text));
            origin.Y += textSize.Y + 2 * dpi;
        }
    }
}
