using System.Globalization;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectCreationPanel
{
    private const string RecentProjectGroupPath = "\0recent-projects";
    private readonly ProjectTemplateGroup recentProjectGroup = new("Recent projects", RecentProjectGroupPath);
    private bool recentProjectCategory;
    private string? selectedRecentProject, recentSearchMatch, recentFocusPath;
    private readonly List<string> visibleRecentProjects = [];
    private readonly NativeListTextSearch recentSearch = new();
    private readonly record struct RecentProjectRow(string Path, uint Id, float Top, float Bottom);
    private List<RecentProjectRow> recentRows = [], nextRecentRows = [];
    private int recentRowsFrame = -1;
    private float recentRowsScale;
    private bool recentScrollFocus;

    private void ResetRecentProjectSelection()
    {
        recentProjectCategory = selectionSource != null && selectionSource.RecentProjects.Count > 0;
        if (recentProjectCategory) groupPath = RecentProjectGroupPath;
        selectedRecentProject = recentSearchMatch = recentFocusPath = null;
        recentRows.Clear(); nextRecentRows.Clear(); visibleRecentProjects.Clear(); recentSearch.Reset(); recentRowsFrame = -1;
    }
    private void DrawRecentProjectGroup()
    {
        var id = EditorLocalization.Text("Recent projects") + "###TemplateGroup/RecentProjects";
        var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (recentProjectCategory) flags |= ImGuiTreeNodeFlags.Selected;
        using (EditorTemplateTreeInput.SuppressSelectionKeyToggle(id)) Gui.TreeNodeEx(id, flags);
        RememberTemplateTreeRow(recentProjectGroup);
        if (Gui.IsItemClicked() || EditorTemplateTreeInput.SelectsCurrentRow()) SelectTemplateGroup(RecentProjectGroupPath, Gui.GetIO().KeyCtrl);
    }
    private void DrawProjectSelectionSourceActions(bool blocked)
    {
        if (selectionSource == null) return;
        using var disabled = ImGuiScopes.Disabled(blocked || parameterStage != null || IsGenerating || generatedProject != null);
        var right = Gui.GetCursorScreenPos().X + Gui.GetContentRegionAvail().X;
        if (DrawProjectSelectionButton(EditorLocalization.Widget("Browse for existing project", "Button")) && selectionSource.BrowseExistingProject(AcceptedLocation))
            closeCreationAfterParameters = true;
        var caption = EditorLocalization.Widget("Reload last session automatically at startup", "Button");
        if (right - Gui.GetItemRectMax().X >= ProjectSelectionCheckboxWidth(caption) + Gui.GetStyle().ItemSpacing.X) Gui.SameLine();
        var reload = selectionSource.ReloadLastSession;
        if (presentation?.Checkbox(caption, ref reload) ?? Gui.Checkbox(caption, ref reload)) selectionSource.ReloadLastSession = reload;
        var buttons = ProjectSelectionButtonWidth(EditorLocalization.Widget("Select", "Button"))
            + ProjectSelectionButtonWidth(EditorLocalization.Widget("Cancel", "Button")) + Gui.GetStyle().ItemSpacing.X * 2;
        if (right - Gui.GetItemRectMax().X >= buttons) Gui.SameLine();
    }
    private Vector2 ProjectSelectionCaptionSize(string label)
    {
        var identity = label.IndexOf("##", StringComparison.Ordinal);
        var text = identity < 0 ? label : label[..identity];
        return presentation?.MeasureCaption(text) ?? Gui.CalcTextSize(text);
    }
    private float ProjectSelectionButtonWidth(string label) => ProjectSelectionCaptionSize(label).X + 40 * Gui.GetStyle().FontScaleDpi;
    private float ProjectSelectionCheckboxWidth(string label) => ProjectSelectionCaptionSize(label).X + Gui.GetFrameHeight() + Gui.GetStyle().ItemInnerSpacing.X;
    private bool DrawProjectSelectionButton(string label)
    {
        if (selectionSource == null) return Gui.Button(label);
        var size = new Vector2(ProjectSelectionButtonWidth(label), ProjectSelectionCaptionSize(label).Y + 12 * Gui.GetStyle().FontScaleDpi);
        return presentation?.ParameterButton(label, size) ?? Gui.Button(label, size);
    }
    private float ProjectSelectionFooterExtraHeight(float width)
    {
        if (selectionSource == null) return 0;
        var spacing = Gui.GetStyle().ItemSpacing.X;
        var browse = ProjectSelectionButtonWidth(EditorLocalization.Widget("Browse for existing project", "Button"));
        var checkbox = ProjectSelectionCheckboxWidth(EditorLocalization.Widget("Reload last session automatically at startup", "Button"));
        var buttons = ProjectSelectionButtonWidth(EditorLocalization.Widget("Select", "Button")) + ProjectSelectionButtonWidth(EditorLocalization.Widget("Cancel", "Button")) + spacing;
        var rows = 0; var used = browse;
        if (used + spacing + checkbox <= width) used += spacing + checkbox; else { rows++; used = checkbox; }
        if (used + spacing + buttons > width) rows++;
        var rowHeight = new[] { "Browse for existing project", "Reload last session automatically at startup", "Select", "Cancel" }
            .Max(text => ProjectSelectionCaptionSize(EditorLocalization.Widget(text, "Button")).Y);
        return rows * (rowHeight + 12 * Gui.GetStyle().FontScaleDpi + Gui.GetStyle().ItemSpacing.Y);
    }
    private unsafe void BeginRecentProjectNavigation()
    {
        visibleRecentProjects.Clear(); nextRecentRows.Clear(); recentFocusPath = null;
        foreach (var path in selectionSource!.RecentProjects)
            if (Path.GetFileNameWithoutExtension(path).Contains(filter, StringComparison.OrdinalIgnoreCase)) visibleRecentProjects.Add(path);
        if (selectedRecentProject != null && !selectionSource.RecentProjects.Contains(selectedRecentProject, StringComparer.OrdinalIgnoreCase)) selectedRecentProject = null;
        var coherent = recentRowsFrame == Gui.GetFrameCount() - 1 && recentRowsScale == Gui.GetStyle().FontScaleDpi && recentRows.Count == visibleRecentProjects.Count;
        if (coherent)
            for (var index = 0; index < recentRows.Count; index++)
                if (!string.Equals(recentRows[index].Path, visibleRecentProjects[index], StringComparison.OrdinalIgnoreCase)) { coherent = false; break; }
        if (!coherent) { recentSearch.Reset(); recentSearchMatch = null; }
        var io = Gui.GetIO();
        if (!coherent || !Gui.IsWindowFocused() || io.WantTextInput || io.KeyAlt || io.KeySuper) return;
        var focused = recentRows.FindIndex(row => row.Id == EditorTemplateTreeInput.FocusedIdentity);
        if (focused < 0 || recentRows.Count == 0) return;
        var target = -1; recentScrollFocus = true;
        if (Gui.IsKeyPressed(ImGuiKey.Home)) target = 0;
        else if (Gui.IsKeyPressed(ImGuiKey.End)) target = recentRows.Count - 1;
        else if (Gui.IsKeyPressed(ImGuiKey.PageUp) || Gui.IsKeyPressed(ImGuiKey.PageDown))
        { target = RecentProjectPageTarget(focused, Gui.IsKeyPressed(ImGuiKey.PageDown)); recentScrollFocus = false; }
        if (target >= 0) { recentFocusPath = visibleRecentProjects[target]; if (!io.KeyCtrl) selectedRecentProject = recentFocusPath; }
        var now = Gui.GetTime(); recentSearch.Expire(now);
        if (recentSearchMatch != null) recentSearch.MatchedIndex = visibleRecentProjects.FindIndex(path => string.Equals(path, recentSearchMatch, StringComparison.OrdinalIgnoreCase));
        var timeout = WindowsListInputTiming.SearchTimeoutSeconds;
        if (Gui.IsKeyPressed(ImGuiKey.Backspace)) recentSearch.Backspace(now, timeout);
        for (var character = 0; character < io.InputQueueCharacters.Size; character++)
        {
            if (!Rune.TryCreate((int)io.InputQueueCharacters[character], out var rune) || Rune.IsControl(rune)) continue;
            if (rune.Value == ' ' && recentSearch.Prefix.Length == 0) continue;
            var match = recentSearch.Search(rune.ToString(), visibleRecentProjects.Count, index => Path.GetFileNameWithoutExtension(visibleRecentProjects[index]),
                now, timeout, CultureInfo.GetCultureInfo("en-US"));
            if (match < 0) continue;
            recentSearchMatch = visibleRecentProjects[match];
            if (recentSearch.ShouldNavigate) { selectedRecentProject = recentFocusPath = recentSearchMatch; recentScrollFocus = true; }
        }
    }
    private int RecentProjectPageTarget(int focused, bool down)
    {
        var padding = Gui.GetCursorPosY(); var height = Math.Max(1, Gui.GetContentRegionAvail().Y); var top = Gui.GetScrollY() + padding;
        var current = recentRows[focused];
        if (current.Top < top) top = current.Top; else if (current.Bottom > top + height) top = current.Bottom - height;
        int Boundary(float pageTop)
        {
            var found = -1;
            for (var index = 0; index < recentRows.Count; index++)
                if (recentRows[index].Top >= pageTop && recentRows[index].Bottom <= pageTop + height)
                { found = index; if (!down) break; }
            if (found >= 0) return found;
            for (var index = 0; index < recentRows.Count; index++)
                if (recentRows[index].Bottom > pageTop && recentRows[index].Top < pageTop + height)
                { found = index; if (!down) break; }
            return found >= 0 ? found : down ? recentRows.Count - 1 : 0;
        }
        if (focused == Boundary(top)) top = Math.Clamp(top + (down ? height : -height), padding, Gui.GetScrollMaxY() + padding);
        Gui.SetScrollY(Math.Max(0, top - padding)); return Boundary(top);
    }
    private void DrawRecentProjects()
    {
        if (selectionSource == null) return;
        BeginRecentProjectNavigation();
        foreach (var path in visibleRecentProjects)
        {
            Gui.PushID(path);
            try
            {
                var scale = Gui.GetStyle().FontScaleDpi; var origin = Gui.GetCursorScreenPos();
                var input = Gui.GetIO(); bool clicked, deselect;
                using (EditorTemplateTreeInput.SuppressListTextSearchSpace("##RecentProject", recentSearch.Prefix.Length != 0
                    && !input.KeyCtrl && !input.KeyShift && !input.KeyAlt && !input.KeySuper))
                    clicked = EditorIconStripInput.Draw("##RecentProject", string.Equals(selectedRecentProject, path, StringComparison.OrdinalIgnoreCase), out deselect,
                        new Vector2(0, 56 * scale), ImGuiSelectableFlags.AllowOverlap);
                var submit = clicked && Gui.IsItemHovered() && ProjectModalOwnsInput() && Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                var offset = Gui.GetScrollY() - Gui.GetWindowPos().Y;
                nextRecentRows.Add(new RecentProjectRow(path, Gui.GetItemID(), Gui.GetItemRectMin().Y + offset, Gui.GetItemRectMax().Y + offset));
                if (string.Equals(recentFocusPath, path, StringComparison.OrdinalIgnoreCase))
                { EditorTemplateTreeInput.FocusCurrentRow(Gui.GetItemID()); if (recentScrollFocus) Gui.SetScrollHereY(); }
                if (Gui.IsItemFocused()) EditorTemplateTreeInput.ClaimListBoundaryKeys(Gui.GetItemID());
                if (Gui.IsItemVisible()) selectionSource.RecentRow(path, origin, Gui.GetItemRectMax());
                if (clicked) selectedRecentProject = deselect ? null : path;
                if (selectionSource.RecentContextMenu(path, AcceptedLocation)) closeCreationAfterParameters = true;
                if (submit) Begin();
            }
            finally { Gui.PopID(); }
        }
        if (selectedRecentProject != null && !selectionSource.RecentProjects.Contains(selectedRecentProject, StringComparer.OrdinalIgnoreCase)) selectedRecentProject = null;
        (recentRows, nextRecentRows) = (nextRecentRows, recentRows); recentRowsFrame = Gui.GetFrameCount(); recentRowsScale = Gui.GetStyle().FontScaleDpi;
    }
}
