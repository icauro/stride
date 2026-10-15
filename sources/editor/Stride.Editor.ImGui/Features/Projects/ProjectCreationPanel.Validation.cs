using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectCreationPanel
{
    private string? projectValidationMessage;
    private bool openProjectValidationMessage;
    private int projectValidationAcceptedFrame = -1;
    private bool ValidateProjectDestination()
    {
        if (selected == null) return false;
        try
        {
            _ = selectionSource != null ? ProjectTemplateService.ValidateSolutionDestination(name, location, new(solutionName, solutionLocation))
                : ProjectTemplateService.ValidateDestination(name, location);
            error = null; return true;
        }
        catch (Exception failure)
        {
            projectValidationMessage = failure.Message;
            openProjectValidationMessage = true;
            error = null;
            return false;
        }
    }
    private void DrawProjectValidationMessage(IProjectCreationPresentation? presenter, Action<string, string> fallback)
    {
        var title = EditorLocalization.Widget("Stride Game Studio") + "###ProjectTemplateValidation";
        if (openProjectValidationMessage) { Gui.OpenPopup(title); openProjectValidationMessage = false; }
        using var modal = ImGuiScopes.Modal(title, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        if (projectValidationMessage == null) { Gui.CloseCurrentPopup(); return; }
        if (presenter != null) presenter.ValidationMessage(projectValidationMessage);
        else fallback("##ProjectValidationError", projectValidationMessage);
        var accept = presenter?.ValidationAccept() ?? Gui.Button(EditorLocalization.Widget("OK", "Button"));
        if (accept || Gui.IsKeyPressed(ImGuiKey.Enter) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter) || Gui.IsKeyPressed(ImGuiKey.Escape))
        { projectValidationMessage = null; projectValidationAcceptedFrame = Gui.GetFrameCount(); Gui.CloseCurrentPopup(); }
    }
    private void ClearTemplateSelection()
    {
        if (!CommitProjectFieldDrafts(validate: true)) return;
        selected = null; values.Clear(); packs.Clear(); packDiscovery = null; hdrProfileDependency = false;
        if (!nameWasEdited) name = "";
    }
    private void SelectTemplateGroup(string path, bool toggle)
    {
        if (!CommitProjectFieldDrafts(validate: true)) return;
        hasSelectedGroup = !(toggle && hasSelectedGroup && groupPath == path);
        groupPath = path;
        recentProjectCategory = hasSelectedGroup && path == RecentProjectGroupPath;
        if (!recentProjectCategory) selectedRecentProject = null;
        if (selected != null && (!hasSelectedGroup
            || groups?.Find(path)?.Descendants.Any(choice => choice.Identity == selected.Identity) != true))
            ClearTemplateSelection();
    }
}
