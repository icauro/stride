using System.Globalization;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectCreationPanel
{
    private readonly record struct TemplateListRow(ProjectTemplateChoice Choice, uint Id, float Top, float Bottom);
    private readonly List<ProjectTemplateChoice> visibleTemplateChoices = [];
    private List<TemplateListRow> templateListRows = [], nextTemplateListRows = [];
    private readonly NativeListTextSearch templateListSearch = new();
    private string? templateSearchMatch, templateListFocus;
    private bool templateListScrollFocus;
    private int templateListFrame = -1;
    private float templateListScale;

    private void ResetTemplateListNavigation()
    {
        visibleTemplateChoices.Clear(); templateListRows.Clear(); nextTemplateListRows.Clear();
        templateListSearch.Reset(); templateSearchMatch = templateListFocus = null; templateListFrame = -1;
    }

    private unsafe void BeginTemplateListNavigation()
    {
        visibleTemplateChoices.Clear(); nextTemplateListRows.Clear(); templateListFocus = null;
        foreach (var choice in hasSelectedGroup ? groups?.Find(groupPath)?.Descendants ?? choices : Array.Empty<ProjectTemplateChoice>())
            if (choice.Description.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) visibleTemplateChoices.Add(choice);
        var coherent = templateListFrame == Gui.GetFrameCount() - 1 && templateListScale == Gui.GetStyle().FontScaleDpi
            && templateListRows.Count == visibleTemplateChoices.Count;
        if (coherent)
            for (var index = 0; index < templateListRows.Count; index++)
                if (!ReferenceEquals(templateListRows[index].Choice, visibleTemplateChoices[index])) { coherent = false; break; }
        if (!coherent) { templateListSearch.Reset(); templateSearchMatch = null; }
        var io = Gui.GetIO();
        if (!coherent || !Gui.IsWindowFocused() || io.WantTextInput || io.KeyAlt || io.KeySuper) return;
        var focused = templateListRows.FindIndex(row => row.Id == EditorTemplateTreeInput.FocusedIdentity);
        if (focused < 0 || templateListRows.Count == 0) return;
        var target = -1;
        templateListScrollFocus = true;
        if (Gui.IsKeyPressed(ImGuiKey.Home)) target = 0;
        else if (Gui.IsKeyPressed(ImGuiKey.End)) target = templateListRows.Count - 1;
        else if (Gui.IsKeyPressed(ImGuiKey.PageUp) || Gui.IsKeyPressed(ImGuiKey.PageDown))
        {
            target = TemplateListPageTarget(focused, Gui.IsKeyPressed(ImGuiKey.PageDown));
            templateListScrollFocus = false;
        }
        if (target >= 0) FocusTemplateListChoice(target, select: !io.KeyCtrl);

        var now = Gui.GetTime(); templateListSearch.Expire(now);
        if (templateSearchMatch != null)
        {
            var match = visibleTemplateChoices.FindIndex(choice => choice.Identity == templateSearchMatch);
            if (match < 0) { templateListSearch.Reset(); templateSearchMatch = null; }
            else templateListSearch.MatchedIndex = match;
        }
        var timeout = WindowsListInputTiming.SearchTimeoutSeconds;
        if (Gui.IsKeyPressed(ImGuiKey.Backspace)) templateListSearch.Backspace(now, timeout);
        for (var character = 0; character < io.InputQueueCharacters.Size; character++)
        {
            if (!Rune.TryCreate((int)io.InputQueueCharacters[character], out var rune) || Rune.IsControl(rune)) continue;
            if (rune.Value == ' ' && templateListSearch.Prefix.Length == 0) continue;
            var match = templateListSearch.Search(rune.ToString(), visibleTemplateChoices.Count,
                index => visibleTemplateChoices[index].Description.Name, now, timeout, CultureInfo.GetCultureInfo("en-US"));
            if (match < 0) continue;
            templateSearchMatch = visibleTemplateChoices[match].Identity;
            if (templateListSearch.ShouldNavigate)
            { templateListScrollFocus = true; FocusTemplateListChoice(match, select: true); }
        }
    }

    private int TemplateListPageTarget(int focused, bool down)
    {
        var paddingTop = Gui.GetCursorPosY();
        var height = Math.Max(1, Gui.GetContentRegionAvail().Y);
        var top = Gui.GetScrollY() + paddingTop;
        var current = templateListRows[focused];
        // Native ItemsControl first makes an offscreen focused item visible.
        if (current.Top < top) top = current.Top;
        else if (current.Bottom > top + height) top = current.Bottom - height;
        var boundary = TemplateListPageBoundary(top, height, down);
        // The first Page press selects the current page edge. Only a press
        // already at that edge advances the viewport, as NavigateByPage does.
        if (focused == boundary)
            top = Math.Clamp(top + (down ? height : -height), paddingTop, Gui.GetScrollMaxY() + paddingTop);
        Gui.SetScrollY(Math.Max(0, top - paddingTop));
        return TemplateListPageBoundary(top, height, down);
    }

    private int TemplateListPageBoundary(float top, float height, bool down)
    {
        var boundary = -1;
        for (var index = 0; index < templateListRows.Count; index++)
        {
            var row = templateListRows[index];
            if (row.Top >= top && row.Bottom <= top + height)
            { boundary = index; if (!down) break; }
        }
        if (boundary >= 0) return boundary;
        for (var index = 0; index < templateListRows.Count; index++)
            if (templateListRows[index].Bottom > top && templateListRows[index].Top < top + height)
            { boundary = index; if (!down) break; }
        return boundary >= 0 ? boundary : down ? templateListRows.Count - 1 : 0;
    }

    private void FocusTemplateListChoice(int index, bool select)
    {
        var choice = visibleTemplateChoices[index];
        templateListFocus = choice.Identity;
        if (select) Select(choice);
    }

    private void RememberTemplateListRow(ProjectTemplateChoice choice)
    {
        var offset = Gui.GetScrollY() - Gui.GetWindowPos().Y;
        nextTemplateListRows.Add(new TemplateListRow(choice, Gui.GetItemID(), Gui.GetItemRectMin().Y + offset, Gui.GetItemRectMax().Y + offset));
        if (choice.Identity == templateListFocus)
        {
            EditorTemplateTreeInput.FocusCurrentRow(Gui.GetItemID());
            if (templateListScrollFocus) Gui.SetScrollHereY();
        }
        if (!Gui.IsItemFocused()) return;
        EditorTemplateTreeInput.ClaimListBoundaryKeys(Gui.GetItemID());
    }

    private void EndTemplateListNavigation()
    {
        (templateListRows, nextTemplateListRows) = (nextTemplateListRows, templateListRows);
        templateListFrame = Gui.GetFrameCount();
        templateListScale = Gui.GetStyle().FontScaleDpi;
    }
}
