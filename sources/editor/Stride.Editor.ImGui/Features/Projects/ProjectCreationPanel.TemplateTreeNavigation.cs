using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectCreationPanel
{
    private readonly record struct TemplateTreeRow(string Path, uint Id, bool HasChildren);
    private List<TemplateTreeRow> templateTreeRows = [], nextTemplateTreeRows = [];
    private ProjectTemplateGroup? templateTreeGeneration;
    private int templateTreeFrame = -1;
    private int templateTreeCancelledFrame = -1;
    private string? templateTreeFocusPath, templateTreeExpansionPath;
    private bool templateTreeExpansion;

    private void ResetTemplateTreeNavigation()
    {
        templateTreeRows.Clear(); nextTemplateTreeRows.Clear(); templateTreeGeneration = null;
        templateTreeFocusPath = templateTreeExpansionPath = null; templateTreeFrame = templateTreeCancelledFrame = -1;
    }

    private void BeginTemplateTreeNavigation()
    {
        nextTemplateTreeRows.Clear(); templateTreeFocusPath = templateTreeExpansionPath = null;
        if (!ReferenceEquals(templateTreeGeneration, groups) || templateTreeFrame != Gui.GetFrameCount() - 1
            || !Gui.IsWindowFocused() || Gui.GetIO().WantTextInput || Gui.GetIO().KeyAlt || Gui.GetIO().KeySuper) return;
        var focused = EditorTemplateTreeInput.FocusedIdentity;
        var index = templateTreeRows.FindIndex(row => row.Id == focused);
        if (index < 0) return;
        var current = templateTreeRows[index];
        if (Gui.IsKeyPressed(ImGuiKey.Escape, false)) templateTreeCancelledFrame = Gui.GetFrameCount();
        if (Gui.IsKeyPressed(ImGuiKey.Home) || Gui.IsKeyPressed(ImGuiKey.End))
        {
            var target = templateTreeRows[Gui.IsKeyPressed(ImGuiKey.Home) ? 0 : templateTreeRows.Count - 1];
            // Native SelectFirst/Last use Single selection even with Control;
            // Control-arrow focus-only behavior remains the native nav adapter.
            SelectTemplateGroup(target.Path, Gui.GetIO().KeyCtrl);
            templateTreeFocusPath = target.Path;
        }
        else if (Gui.IsKeyPressed(ImGuiKey.PageUp) || Gui.IsKeyPressed(ImGuiKey.PageDown))
        {
            // TreeViewItem does not handle Page keys: its ScrollViewer pages
            // the viewport without selecting or focusing a different group.
            var page = Math.Max(1, Gui.GetContentRegionAvail().Y);
            Gui.SetScrollY(Math.Clamp(Gui.GetScrollY() + (Gui.IsKeyPressed(ImGuiKey.PageUp) ? -page : page), 0, Gui.GetScrollMaxY()));
        }
        if (current.HasChildren && (Gui.IsKeyPressed(ImGuiKey.KeypadAdd) || Gui.IsKeyPressed(ImGuiKey.KeypadSubtract)))
        { templateTreeExpansionPath = current.Path; templateTreeExpansion = Gui.IsKeyPressed(ImGuiKey.KeypadAdd); }
    }

    private void ApplyTemplateTreeExpansion(ProjectTemplateGroup group)
    {
        if (group.Path == templateTreeExpansionPath) Gui.SetNextItemOpen(templateTreeExpansion);
    }

    private void RememberTemplateTreeRow(ProjectTemplateGroup group)
    {
        nextTemplateTreeRows.Add(new TemplateTreeRow(group.Path, Gui.GetItemID(), group.Children.Count != 0));
        if (group.Path == templateTreeFocusPath)
        {
            EditorTemplateTreeInput.FocusCurrentRow(Gui.GetItemID());
            Gui.SetScrollHereY(group == groups || group == recentProjectGroup ? 0 : 1);
        }
        if (Gui.IsItemFocused()) EditorTemplateTreeInput.ClaimBoundaryKeys(Gui.GetItemID());
    }

    private void EndTemplateTreeNavigation()
    {
        (templateTreeRows, nextTemplateTreeRows) = (nextTemplateTreeRows, templateTreeRows);
        templateTreeFrame = Gui.GetFrameCount(); templateTreeGeneration = groups;
    }
}
