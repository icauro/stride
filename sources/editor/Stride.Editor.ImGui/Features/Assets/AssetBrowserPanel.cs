using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Owns asset-browser presentation and transient UI state; authored commands stay behind the host bridge.</summary>
internal sealed partial class AssetBrowserPanel
{
    internal bool Visible = true;
    internal bool FocusRequested;
    internal bool DrawWindow()
    {
        assetDropTargetVisible = false;
        if (!Visible) return false;
        if (FocusRequested) { Gui.SetNextWindowFocus(); FocusRequested = false; }
        using var window = ImGuiScopes.Window(EditorPanels.Assets.DisplayWindowName, ref Visible);
        if (!window.DrawContents) return false;
        var docked = Gui.IsWindowDocked();
        Draw();
        return docked;
    }
    private readonly IAssetBrowserPanelHost host;
    private readonly AssetBrowserState assetBrowser;
    private ProjectWorkspace? filterWorkspace;
    private readonly AssetBrowserTextSearch assetTextSearch = new();
    private string assetFilter = "";
    private string tagFilterDraft = "";
    private bool assetTileView = true;
    private int assetTileThumbnailSize = 96;
    private int assetGridThumbnailSize = 16;
    private NVector2 assetDropMin, assetDropMax;
    private bool assetDropTargetVisible;
    internal bool TryHandleFileDrop(string[] files, NVector2 position)
    {
        if (!Visible || !assetDropTargetVisible || !host.CanImportAssets || position.X < assetDropMin.X || position.Y < assetDropMin.Y
            || position.X >= assetDropMax.X || position.Y >= assetDropMax.Y) return false;
        host.Execute(() => host.RequestImportFiles(files));
        return true;
    }
    public string SearchText { get => assetFilter; set => assetFilter = value; }
    public bool TileView { get => assetTileView; set => assetTileView = value; }
    public int ThumbnailSize { get => assetTileThumbnailSize; set => assetTileThumbnailSize = value; }
    public int GridThumbnailSize { get => assetGridThumbnailSize; set => assetGridThumbnailSize = value; }
    public AssetBrowserPanel(IAssetBrowserPanelHost host, AssetBrowserState assetBrowser)
    {
        this.host = host; this.assetBrowser = assetBrowser;
        assetBrowser.Sort = AssetBrowserSort.TypeThenName;
    }
    public void Draw()
    {
        assetBrowser.Bind(host.Workspace);
        if (!ReferenceEquals(filterWorkspace, host.Workspace))
        { filterWorkspace = host.Workspace; assetTextSearch.Reset(); tileLabels.Clear(); tileMetricsKey = null; tableMetricsKey = null; assetFilter = tagFilterDraft = ""; assetBrowser.RestoreFilters(host.SavedFilters); assetBrowser.Sort = host.SavedSort; assetBrowser.DisplayMode = host.SavedDisplayMode; assetGridThumbnailSize = host.SavedGridThumbnailSize; }
        if (host.Workspace == null) { DrawAssetToolbar(); return; }
        host.RefreshFolders();
        host.BeginThumbnails();
        host.SyncAuthoredTags();
        DrawAssetToolbar();
        var zoomDirection = AssetBrowserZoomGesture.ReadDirection();
        if (zoomDirection != 0) ZoomAssetThumbnails(zoomDirection);
        assetDropMin = Gui.GetCursorScreenPos(); assetDropMax = assetDropMin + Gui.GetContentRegionAvail();
        assetDropTargetVisible = host.Workspace != null;
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.GetIO().AppFocusLost && !Gui.GetIO().WantTextInput
            && !Gui.IsAnyItemActive() && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.A, false))
            host.Execute(() => { assetBrowser.SelectAllContent(); host.OnAssetSelectionChanged(); });
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.GetIO().WantTextInput
            && !Gui.IsAnyItemActive() && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && !Gui.GetIO().AppFocusLost && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeySuper
            && Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.C, false) && (Gui.GetIO().KeyShift ? host.CanCopyAssetDependencies : host.CanCopyAssets))
        {
            var dependencies = Gui.GetIO().KeyShift;
            host.Execute(() => host.CopyAssets(dependencies));
        }
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.GetIO().WantTextInput
            && !Gui.IsAnyItemActive() && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.X, false) && host.CanCutAssets)
            host.Execute(host.CutAssets);
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.GetIO().WantTextInput
            && !Gui.IsAnyItemActive() && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.V, false) && host.CanPasteAssets)
            host.Execute(host.PasteAssets);
        var editInput = Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.GetIO().AppFocusLost
            && !Gui.GetIO().WantTextInput && !Gui.IsAnyItemActive() && !Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyShift
            && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeySuper && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
        if (editInput && Gui.IsKeyPressed(ImGuiKey.Delete, false)) host.RequestDeletion();
        if (editInput && Gui.IsKeyPressed(ImGuiKey.F2) && assetBrowser.SelectedContentFolders.Count == 0
            && assetBrowser.SelectedCount == 1 && assetBrowser.PrimaryEntry is { } rename) host.RequestRename(rename);
        if (editInput && Gui.IsKeyPressed(ImGuiKey.F2) && assetBrowser.SelectedCount == 0
            && assetBrowser.SelectedContentFolders is { Count: 1 } folders) host.RequestFolderRename(folders[0]);
        AssetEntry? clicked = null;
        AssetBrowserContent? clickedContent = null;
        var toggle = false; var range = false;
        var openSelection = false;
        Action? assetAction = null;
        if (assetTileView) DrawTiles(ref clicked, ref clickedContent, ref toggle, ref range, ref openSelection, ref assetAction);
        if (!assetTileView && Gui.BeginTable("AssetTable", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, Gui.GetContentRegionAvail()))
        {
            Gui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch);
            Gui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 130);
            Gui.TableHeadersRow();
            var rows = assetBrowser.VisibleContent;
            var clipper = Gui.ImGuiListClipper();
            try
            {
                // Native table-aware clipping measures the first uniform row, then
                // seeks to visible ranges without submitting every asset widget.
                Gui.Begin(clipper, rows.Count);
                if (assetBrowser.RevealTarget is { } reveal)
                    for (var index = 0; index < rows.Count; index++)
                        if (rows[index].Asset?.Id == reveal) { Gui.IncludeItemByIndex(clipper, index); break; }
                while (Gui.Step(clipper))
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var content = rows[index];
                    if (content.Folder is { } folder)
                    {
                        DrawFolderTableRow(content, folder, ref clickedContent, ref toggle, ref range, ref openSelection, ref assetAction);
                        continue;
                    }
                    var asset = content.Asset!;
                    var rowHeight = BrowserTableRowHeight();
                    Gui.TableNextRow(ImGuiTableRowFlags.None, rowHeight); Gui.TableNextColumn();
                    Gui.PushID(asset.UiId);
                    var origin = Gui.GetCursorScreenPos();
                    var labelWidth = Gui.GetContentRegionAvail().X;
                    var editingName = host.DrawRenameField(asset);
                    if (!editingName && Gui.Selectable("##Asset", assetBrowser.IsSelected(asset.Id), ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick, new NVector2(0, rowHeight)))
                    {
                        clicked = asset; clickedContent = content; toggle = Gui.GetIO().KeyCtrl; range = Gui.GetIO().KeyShift;
                        openSelection = Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                    }
                    if (assetBrowser.RevealTarget == asset.Id) { Gui.SetScrollHereY(0.5f); assetBrowser.CompleteReveal(); }
                    if (host.CaptureControls && Gui.IsItemVisible()) host.RecordControl("asset/" + asset.Name);
                    var rowMaximum = Gui.GetItemRectMax();
                    var drawList = Gui.GetWindowDrawList();
                    var thumbnailSize = assetGridThumbnailSize * Gui.GetStyle().FontScaleDpi;
                    if (!editingName)
                    {
                        host.DrawThumbnail(asset, origin, thumbnailSize);
                        var thumbnailOffset = thumbnailSize + Gui.GetStyle().ItemSpacing.X;
                        origin.X += thumbnailOffset; labelWidth = Math.Max(0, labelWidth - thumbnailOffset);
                    }
                    drawList.PushClipRect(origin, new NVector2(origin.X + labelWidth, rowMaximum.Y), true);
                    if (!editingName)
                    {
                        var name = AssetBrowserNames.Display(asset) + (host.IsDirty(asset) ? "*" : "");
                        var size = host.MeasureLabel(name);
                        var nameOrigin = origin + new NVector2(0, Math.Max(0, (rowHeight - size.Y) / 2));
                        host.PaintLabel(name, nameOrigin, origin, new NVector2(origin.X + labelWidth, rowMaximum.Y), Gui.GetColorU32(ImGuiCol.Text));
                    }
                    drawList.PopClipRect();
                    var hovered = Gui.IsItemHovered();
                    if (!editingName) host.DrawDragSource(asset);
                    if (Gui.BeginPopupContextItem("Asset actions"))
                    {
                        if (!assetBrowser.IsSelected(asset.Id)) { clicked = asset; clickedContent = content; toggle = range = false; openSelection = false; }
                        assetAction = host.DrawActions(asset) ?? assetAction;
                        Gui.EndPopup();
                    }
                    if (hovered && !Gui.IsPopupOpen("Asset actions"))
                    {
                        host.DrawTooltip(asset);
                    }
                    Gui.TableNextColumn();
                    Gui.PushStyleColor(ImGuiCol.Text, Gui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                    host.DrawLabel(asset.TypeLabel); Gui.PopStyleColor(); Gui.PopID();
                }
                DrawAssetBackgroundContext(ref assetAction);
            }
            finally { Gui.Destroy(clipper); Gui.EndTable(); }
        }
        if (clickedContent != null)
        {
            assetBrowser.SelectContent(clickedContent, toggle, range);
            host.OnAssetSelectionChanged();
            if (clickedContent.Folder is { } navigate && openSelection) { assetBrowser.SelectFolder(navigate); }
            else if (assetBrowser.SelectedContentFolders.Count == 0 && assetBrowser.SelectedCount == 1 && assetBrowser.PrimaryEntry is { } primary)
            {
                if (openSelection) host.OpenAsset(primary);
                else host.PreviewAsset(primary);
            }
        }
        if (assetAction != null) host.Execute(assetAction);
        host.ApplyPendingRelocation();
    }
}
