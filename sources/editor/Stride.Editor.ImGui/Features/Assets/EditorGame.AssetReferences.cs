using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool showAssetReferences = true;
    private readonly AssetReferencesState assetReferences = new();
    private bool referenceFiltersLoaded;
    private readonly AssetBrowserTextSearch referenceTextSearch = new();
    private string referenceFilter = "";
    private readonly AssetFilterSearch referenceFilterSearch = new();
    private string referenceTagDraft = "";
    private bool referenceTileView = true;
    private int referenceThumbnailSize = 96;
    private int referenceGridThumbnailSize = 16;
    private void ClearAssetReferenceState()
    {
        assetSortMetadata.Clear(); assetReferences.Reset(); referenceTextSearch.Reset(); referenceFilter = referenceTagDraft = ""; referenceFiltersLoaded = false;
        assetBuildInclusion.Clear(); referenceContextAssets = []; referenceContextWorkspace = null; ClearModifiedSourceStatus();
        referenceBackgroundAssets = []; referenceBackgroundWorkspace = null;
    }
    private void DrawAssetReferences()
    {
        if (!showAssetReferences) return;
        using var contentFont = ImGuiScopes.Font(12);
        Gui.SetNextWindowSize(new System.Numerics.Vector2(UiScale(760), UiScale(200)), ImGuiCond.FirstUseEver);
        Gui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(UiScale(4), 0));
        var windowVisible = Gui.Begin(EditorLocalization.Widget("References", "View"), ref showAssetReferences);
        Gui.PopStyleVar();
        if (!windowVisible) { ImGuiScopes.EndWindow(); return; }
        if (Gui.IsWindowDocked()) dockedPanels++;
        if (workspace != null) Execute(() => { UpdateAssetSortMetadata(); assetReferences.Update(workspace, documents, assetBrowser); });
        else assetReferences.Clear();
        if (!referenceFiltersLoaded && preferences != null)
        { assetReferences.RestoreFilters(preferences.Current.ReferenceFilters ?? preferences.Current.AssetFilters); assetReferences.Sort = preferences.Current.AssetSort; referenceThumbnailSize = preferences.Current.AssetTileThumbnailSize; referenceGridThumbnailSize = preferences.Current.AssetGridThumbnailSize; referenceFiltersLoaded = true; }
        DrawReferenceFilters();
        var zoomDirection = AssetBrowserZoomGesture.ReadDirection();
        if (zoomDirection != 0) ZoomReferenceThumbnails(zoomDirection);
        BeginAssetThumbnails();
        Execute(UpdateAssetBuildInclusion);
        AssetEntry? navigate = null;
        AssetEntry? clicked = null; var toggle = false; var range = false;
        Action? badgeAction = null;
        Action? commandAction = null;
        AssetEntry? contextTarget = null;
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.GetIO().WantTextInput && !Gui.IsAnyItemActive()
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) && Gui.GetIO().KeyCtrl)
        {
            var selection = assetReferences.SelectedEntries;
            if (Gui.IsKeyPressed(ImGuiKey.A, false)) assetReferences.SelectAllVisible();
            else if (Gui.IsKeyPressed(ImGuiKey.C, false) && !Gui.GetIO().AppFocusLost && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeySuper && CanCopyReferenceAssets(selection))
            {
                var dependencies = Gui.GetIO().KeyShift;
                commandAction = () => CopyAssetEntries(selection, dependencies);
            }
            else if (Gui.IsKeyPressed(ImGuiKey.X, false) && CanCutReferenceAssets(selection))
                commandAction = () => CutReferenceAssets(selection);
        }
        var available = Gui.GetContentRegionAvail();
        var footerHeight = Gui.GetTextLineHeight() + UiScale(10);
        var contentSize = new System.Numerics.Vector2(available.X, Math.Max(1, available.Y - footerHeight - Gui.GetStyle().ItemSpacing.Y));
        if (referenceTileView) DrawReferenceTiles(contentSize, ref clicked, ref toggle, ref range, ref navigate, ref badgeAction, ref contextTarget, ref commandAction);
        else if (Gui.BeginTable("ReferenceAssets", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX,
            contentSize,
            Math.Max(available.X, UiScale(320))))
        {
            Gui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch);
            Gui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, UiScale(130)); Gui.TableHeadersRow();
            var rows = assetReferences.Visible; var clipper = Gui.ImGuiListClipper();
            try
            {
                Gui.Begin(clipper, rows.Count);
                while (Gui.Step(clipper))
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var asset = rows[i]; Gui.PushID(asset.UiId); var rowHeight = Math.Max(Gui.GetFrameHeight(), UiScale(referenceGridThumbnailSize)); Gui.TableNextRow(ImGuiTableRowFlags.None, rowHeight); Gui.TableNextColumn();
                        var origin = Gui.GetCursorScreenPos(); var width = Gui.GetContentRegionAvail().X;
                        if (Gui.Selectable("##ReferenceAsset", assetReferences.IsSelected(asset.Id), ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick, new System.Numerics.Vector2(0, rowHeight)))
                        { clicked = asset; toggle = Gui.GetIO().KeyCtrl; range = Gui.GetIO().KeyShift; }
                        if (Gui.IsItemHovered() && Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) navigate = asset;
                        if (Gui.IsItemClicked(ImGuiMouseButton.Right)) contextTarget = asset;
                        var hovered = Gui.IsItemHovered();
                        var list = Gui.GetWindowDrawList();
                        var thumbnail = UiScale(referenceGridThumbnailSize); DrawAssetThumbnail(asset, origin, thumbnail);
                        origin.X += thumbnail + Gui.GetStyle().ItemSpacing.X; width -= thumbnail + Gui.GetStyle().ItemSpacing.X;
                        list.PushClipRect(origin, new System.Numerics.Vector2(origin.X + width, Gui.GetItemRectMax().Y), true);
                        list.AddText(origin, Gui.GetColorU32(ImGuiCol.Text), AssetBrowserNames.Display(asset) + (assetSortMetadata.Get(asset.Id).IsDirty ? "*" : "")); list.PopClipRect();
                        Gui.TableNextColumn(); Gui.TextUnformatted(asset.TypeLabel);
                        if (hovered && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) DrawAssetTooltip(asset);
                        Gui.PopID();
                    }
                DrawReferenceBackgroundContext(ref commandAction);
            }
            finally { Gui.Destroy(clipper); Gui.EndTable(); }
        }
        if (contextTarget != null)
        {
            if (!assetReferences.IsSelected(contextTarget.Id)) assetReferences.Select(contextTarget, false, false);
            referenceContextAssets = assetReferences.SelectedEntries; referenceContextWorkspace = workspace;
            clicked = null; navigate = null; Gui.OpenPopup("Reference asset actions");
        }
        if (Gui.BeginPopup("Reference asset actions"))
        {
            if (!ReferenceEquals(workspace, referenceContextWorkspace) || workspace == null
                || referenceContextAssets.Any(asset => !workspace.AssetItems.Any(item => item.Id == asset.Id && SamePath(item.FullPath.ToOSPath(), asset.Path)))) Gui.CloseCurrentPopup();
            else commandAction = DrawReferenceAssetActions(referenceContextAssets) ?? commandAction;
            Gui.EndPopup();
        }
        var counterOrigin = Gui.GetCursorScreenPos();
        var counterWidth = Gui.GetContentRegionAvail().X;
        Gui.InvisibleButton("##ReferenceCounters", new System.Numerics.Vector2(Math.Max(1, counterWidth), footerHeight));
        var counterDrawList = Gui.GetWindowDrawList();
        counterDrawList.PushClipRect(counterOrigin, new System.Numerics.Vector2(counterOrigin.X + counterWidth, Gui.GetItemRectMax().Y), true);
        counterDrawList.AddText(counterOrigin + new System.Numerics.Vector2(UiScale(6), UiScale(5)), Gui.GetColorU32(ImGuiCol.Text), assetReferences.TypeCounters);
        counterDrawList.PopClipRect();
        if (Gui.IsItemHovered() && assetReferences.TypeCounters.Length > 0) Gui.SetTooltip(assetReferences.TypeCounters);
        ImGuiScopes.EndWindow();
        if (badgeAction != null) Execute(badgeAction);
        if (commandAction != null) Execute(commandAction);
        if (clicked != null) assetReferences.Select(clicked, toggle, range);
        if (navigate != null)
        {
            if (workspace != null && assetBrowser.RevealAsset(workspace, navigate.Id) is { } revealed)
            {
                selectedAssetPath = revealed.Path;
                ActivateAssetPropertyContext();
            }
        }
    }
    private void DrawReferenceFilters()
    {
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(UiScale(4), UiScale(2)));
        var modeHeight = Gui.GetFrameHeight();
        var referencesWidth = Gui.CalcTextSize("References").X + Gui.GetStyle().FramePadding.X * 2;
        var referencersWidth = Gui.CalcTextSize("Referenced by").X + Gui.GetStyle().FramePadding.X * 2;
        if (Gui.Selectable(EditorLocalization.Widget("References"), !assetReferences.ShowReferencers, ImGuiSelectableFlags.None, new System.Numerics.Vector2(referencesWidth, modeHeight)))
            assetReferences.ShowReferencers = false;
        ReferenceToolbarSameLine(referencersWidth);
        if (Gui.Selectable(EditorLocalization.Widget("Referenced by"), assetReferences.ShowReferencers, ImGuiSelectableFlags.None, new System.Numerics.Vector2(referencersWidth, modeHeight)))
            assetReferences.ShowReferencers = true;
        ReferenceToolbarSameLine(modeHeight);
        if (Gui.Button("##ReferenceView", new System.Numerics.Vector2(modeHeight))) Gui.OpenPopup("Reference view options");
        var viewOrigin = Gui.GetItemRectMin() + new System.Numerics.Vector2(UiScale(5));
        var viewList = Gui.GetWindowDrawList(); var square = UiScale(3); var step = UiScale(5);
        for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++)
        {
            var position = viewOrigin + new System.Numerics.Vector2(x * step, y * step);
            viewList.AddRectFilled(position, position + new System.Numerics.Vector2(square), Gui.GetColorU32(ImGuiCol.Text));
        }
        if (Gui.IsItemHovered()) Gui.SetTooltip("Asset view options");
        if (Gui.BeginPopup("Reference view options"))
        {
            if (DrawNativeMenuItem(EditorLocalization.Widget("Name", "Menu"), "", assetReferences.Sort == AssetBrowserSort.Name)) assetReferences.Sort = AssetBrowserSort.Name;
            if (DrawNativeMenuItem(EditorLocalization.Widget("Type", "Menu"), "", assetReferences.Sort == AssetBrowserSort.TypeThenName)) assetReferences.Sort = AssetBrowserSort.TypeThenName;
            if (DrawNativeMenuItem(EditorLocalization.Widget("Unsaved changes", "Menu"), "", assetReferences.Sort == AssetBrowserSort.DirtyThenName)) { assetReferences.Sort = AssetBrowserSort.DirtyThenName; UpdateAssetSortMetadata(); }
            if (DrawNativeMenuItem(EditorLocalization.Widget("Date modified", "Menu"), "", assetReferences.Sort == AssetBrowserSort.ModificationDateThenName)) { assetReferences.Sort = AssetBrowserSort.ModificationDateThenName; UpdateAssetSortMetadata(); }
            Gui.Separator();
            DrawNativeMenuItem(EditorLocalization.Widget("Tiles", "Menu"), "", ref referenceTileView);
            var zoom = referenceTileView ? referenceThumbnailSize : referenceGridThumbnailSize;
            Gui.BeginDisabled(zoom >= 128);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Zoom in", "Menu"))) ZoomReferenceThumbnails(1);
            Gui.EndDisabled();
            Gui.BeginDisabled(zoom <= 16);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Zoom out", "Menu"))) ZoomReferenceThumbnails(-1);
            Gui.EndDisabled(); Gui.EndPopup();
        }
        var hasFilters = assetReferences.Names.Count > 0 || assetReferences.TypeFilterCount > 0 || assetReferences.Tags.Count > 0;
        ReferenceToolbarSameLine(UiScale(60));
        var clearWidth = hasFilters ? modeHeight + Gui.GetStyle().ItemSpacing.X : 0;
        Gui.SetNextItemWidth(Math.Max(UiScale(30), Gui.GetContentRegionAvail().X - modeHeight - clearWidth - Gui.GetStyle().ItemSpacing.X));
        referenceFilterSearch.Draw("##ReferenceFilter", ref referenceFilter, assetReferences.AvailableTypes,
            (choice, replace) => { assetReferences.CommitFilter(choice, replace); SaveReferenceFilters(); });
        Gui.SameLine();
        if (Gui.ArrowButton("##ReferenceFilterOptions", ImGuiDir.Down)) Gui.OpenPopup("Reference filter options");
        if (Gui.BeginPopup("Reference filter options"))
        {
            if (BeginNativeMenu("Types"))
            {
              foreach (var type in assetReferences.AvailableTypes)
              {
                Gui.PushID(type.FullName); var selected = assetReferences.HasType(type);
                if (Gui.Checkbox("##Type", ref selected)) { assetReferences.ToggleType(type); SaveReferenceFilters(); }
                Gui.SameLine(); Gui.TextUnformatted(Stride.Core.DisplayAttribute.GetDisplayName(type) ?? type.Name); Gui.PopID();
              }
              Gui.EndMenu();
            }
            if (BeginNativeMenu("Tags"))
            {
            Gui.SetNextItemWidth(UiScale(210)); Gui.InputTextWithHint("##ReferenceTag", "Tag", ref referenceTagDraft, 256); Gui.SameLine();
            if (Gui.Button("Add")) { assetReferences.AddTag(referenceTagDraft); referenceTagDraft = ""; SaveReferenceFilters(); }
            Gui.EndMenu();
            }
            Gui.EndPopup();
        }
        if (hasFilters)
        {
            Gui.SameLine();
            if (Gui.Button("x##ClearReferenceFilters", new System.Numerics.Vector2(modeHeight)))
            { referenceFilter = referenceTagDraft = ""; assetReferences.ClearFilters(); SaveReferenceFilters(); }
            if (Gui.IsItemHovered()) Gui.SetTooltip("Clear filters");
        }
        Gui.PopStyleVar();
        DrawReferenceFilterChips();
    }
    private void DrawReferenceTiles(System.Numerics.Vector2 size, ref AssetEntry? clicked, ref bool toggle, ref bool range, ref AssetEntry? navigate, ref Action? badgeAction, ref AssetEntry? contextTarget, ref Action? commandAction)
    {
        // WPF's reference AssetView has no border/padding around its tile viewport.
        // Keep a short child viewport usable instead of consuming it with inherited
        // window padding before the first partially visible row can be submitted.
        Gui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        var visible = Gui.BeginChild("Reference tiles", size);
        Gui.PopStyleVar();
        if (!visible) { ImGuiScopes.EndChild(); return; }
        BeginAssetThumbnails();
        var thumbnailSize = UiScale(referenceThumbnailSize);
        var tileWidth = UiScale(Math.Max(64, referenceThumbnailSize) + 18);
        float nameHeight, typeHeight;
        // PushFont takes logical size; ImGui already applies FontScaleDpi.
        using (ImGuiScopes.Font(12)) nameHeight = Math.Max(UiScale(32), MeasureNativeLabel("Mg").Y * 2);
        using (ImGuiScopes.Font(10)) typeHeight = Math.Max(UiScale(14), MeasureNativeLabel("Mg").Y);
        var tileHeight = thumbnailSize + nameHeight + typeHeight;
        var spacing = Gui.GetStyle().ItemSpacing;
        var rowHeight = tileHeight + spacing.Y;
        var columns = Math.Max(1, (int)((Gui.GetContentRegionAvail().X + spacing.X) / (tileWidth + spacing.X)));
        var rows = assetReferences.Visible;
        var left = Gui.GetCursorScreenPos().X;
        var currentIndex = -1;
        for (var index = 0; index < rows.Count; index++)
            if (assetReferences.IsSelected(rows[index].Id)) { currentIndex = index; break; }
        var keyboardTarget = AssetTileKeyboardNavigation.ReadTarget(currentIndex, rows.Count, columns,
            (int)(Gui.GetContentRegionAvail().Y / (tileHeight + spacing.Y * 2)));
        if (referenceTextSearch.ReadTarget(rows, static row => row.UiId, static row => AssetBrowserNames.Display(row)) is { } textTarget)
            keyboardTarget = textTarget;
        if (keyboardTarget is { } target) { clicked = rows[target]; toggle = range = false; }
        var clipper = Gui.ImGuiListClipper();
        try
        {
            Gui.Begin(clipper, (rows.Count + columns - 1) / columns, rowHeight);
            if (keyboardTarget is { } targetIndex) Gui.IncludeItemByIndex(clipper, targetIndex / columns);
            while (Gui.Step(clipper)) for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                var top = Gui.GetCursorScreenPos().Y;
                for (var column = 0; column < columns && row * columns + column < rows.Count; column++)
                {
                    var asset = rows[row * columns + column];
                    var origin = new System.Numerics.Vector2(left + column * (tileWidth + spacing.X), top);
                    Gui.SetCursorScreenPos(origin); Gui.PushID(asset.UiId);
                    if (keyboardTarget == row * columns + column) Gui.SetKeyboardFocusHere();
                    if (Gui.Selectable("##ReferenceTile", assetReferences.IsSelected(asset.Id), ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap, new System.Numerics.Vector2(tileWidth, tileHeight)))
                    { clicked = asset; toggle = Gui.GetIO().KeyCtrl; range = Gui.GetIO().KeyShift; }
                    if (keyboardTarget == row * columns + column) Gui.SetScrollHereY(0.5f);
                    if (Gui.IsItemHovered() && Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) navigate = asset;
                    if (Gui.IsItemClicked(ImGuiMouseButton.Right)) contextTarget = asset;
                    DrawReferenceAssetDragSource(asset);
                    if (Gui.IsItemHovered() && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) DrawAssetTooltip(asset);
                    var thumbnailOrigin = origin + new System.Numerics.Vector2((tileWidth - thumbnailSize) * .5f, 0);
                    if (Gui.IsRectVisible(thumbnailOrigin, thumbnailOrigin + new System.Numerics.Vector2(thumbnailSize)))
                        DrawAssetThumbnail(asset, thumbnailOrigin, thumbnailSize);
                    if (DrawAssetTileBadges(asset, thumbnailOrigin, thumbnailSize) is { } action)
                    {
                        badgeAction = action;
                        if (clicked?.Id == asset.Id) clicked = null;
                        if (navigate?.Id == asset.Id) navigate = null;
                    }
                    var textOrigin = origin + new System.Numerics.Vector2(UiScale(5), thumbnailSize);
                    var width = tileWidth - UiScale(10);
                    var name = AssetBrowserNames.Display(asset) + (assetSortMetadata.Get(asset.Id).IsDirty ? "*" : "");
                    float actualNameHeight;
                    using (ImGuiScopes.Font(12))
                    {
                        actualNameHeight = CanDrawNativeWidgetText ? Math.Min(nameHeight, NativeWidgetLayout(name, width, true).Size.Y)
                            : Math.Min(nameHeight, Gui.CalcTextSize(name, false, width).Y);
                        PaintReferenceTileLabel(name, textOrigin, width, nameHeight, Gui.GetColorU32(ImGuiCol.Text), true);
                    }
                    textOrigin.Y += actualNameHeight;
                    using (ImGuiScopes.Font(10))
                        PaintReferenceTileLabel(asset.TypeLabel, textOrigin, width, typeHeight, Gui.GetColorU32(ImGuiCol.TextDisabled), false);
                    Gui.PopID();
                }
                Gui.SetCursorScreenPos(new System.Numerics.Vector2(left, top));
                Gui.Dummy(new System.Numerics.Vector2(columns * (tileWidth + spacing.X) - spacing.X, tileHeight));
            }
            DrawReferenceBackgroundContext(ref commandAction);
        }
        finally { Gui.Destroy(clipper); ImGuiScopes.EndChild(); }
    }
    private void PaintReferenceTileLabel(string text, System.Numerics.Vector2 origin, float width, float height, uint color, bool wrap)
    {
        if (!CanDrawNativeWidgetText)
        {
            var draw = Gui.GetWindowDrawList();
            draw.PushClipRect(origin, origin + new System.Numerics.Vector2(width, height), true);
            try { Gui.AddText(draw, Gui.GetFont(), Gui.GetFontSize(), origin, color, text, wrap ? width : 0); }
            finally { draw.PopClipRect(); }
            return;
        }
        if (!wrap && MeasureNativeLabel(text).X > width)
        {
            const string ellipsis = "…";
            while (text.Length > 0 && MeasureNativeLabel(text + ellipsis).X > width)
                text = text[..System.Globalization.StringInfo.ParseCombiningCharacters(text)[^1]];
            text += ellipsis;
        }
        var layout = NativeWidgetLayout(text, width, wrap, color);
        PaintNativeWidget(layout, origin + new System.Numerics.Vector2(Math.Max(0, (width - layout.Size.X) * .5f), 0),
            origin, origin + new System.Numerics.Vector2(width, height));
    }
    private static void ReferenceToolbarSameLine(float width)
    {
        var right = Gui.GetCursorScreenPos().X + Gui.GetContentRegionAvail().X;
        if (Gui.GetItemRectMax().X + Gui.GetStyle().ItemSpacing.X + width <= right) Gui.SameLine();
    }
}
