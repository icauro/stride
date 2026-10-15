using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackageDependencyPicker()
    {
        var title = EditorLocalization.Widget("Select a package");
        if (showPackageDependencyPicker) { Gui.OpenPopup(title); showPackageDependencyPicker = false; }
        if (!Gui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (closePackageDependencyPicker)
        { closePackageDependencyPicker = false; Gui.CloseCurrentPopup(); Gui.EndPopup(); return; }
        if (Gui.BeginChild("Packages", new Vector2(UiScale(420), UiScale(520)), ImGuiChildFlags.Borders))
            foreach (var candidate in packageDependencyCandidates)
            {
                Gui.PushID(candidate.Loaded?.FullPath.ToOSPath() ?? candidate.Suggested!.Id + "/" + candidate.Suggested.Version);
                if (Gui.Selectable(candidate.Name, ReferenceEquals(candidate, selectedPackageDependency))) selectedPackageDependency = candidate;
                Gui.PopID();
            }
        ImGuiScopes.EndChild();
        var eligible = standaloneDependencyAddition is { } standalone
            ? standalone.Current(this) : addingPackageDependency != null && packageDependencyPickerContext != null;
        Gui.BeginDisabled(selectedPackageDependency == null || !eligible || IsAuthoringPipelineUpdating || IsPackageDependencyBusy);
        var confirm = Gui.Button(EditorLocalization.Widget("OK", "Button"));
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Gui.SetTooltip(EditorLocalization.Text("Select this package", "ToolTip"));
        Gui.EndDisabled(); Gui.SameLine();
        var cancel = Gui.Button(EditorLocalization.Widget("Cancel", "Button"));
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Cancel (Esc)", "ToolTip"));
        cancel |= Gui.IsKeyPressed(ImGuiKey.Escape);
        if (cancel) Gui.CloseCurrentPopup();
        Gui.EndPopup();
        if (cancel) Execute(() =>
        {
            addingPackageDependency = null; packageDependencyPickerContext = null; selectedPackageDependency = null;
            packageDependencyCandidates = []; ClearStandaloneDependencyAddition();
        });
        else if (confirm) Execute(() =>
        {
            var selected = selectedPackageDependency ?? throw new InvalidOperationException("The dependency target changed.");
            if (!packageDependencyCandidates.Contains(selected)) throw new InvalidOperationException("The dependency target changed.");
            if (standaloneDependencyAddition is { } addition) { CommitStandaloneDependencyAddition(addition, selected); return; }
            var project = addingPackageDependency!;
            packageDependencyPickerContext!.Validate(workspace, SessionAuthoringHistory);
            if (!ReferenceEquals(project, packageDependencyPickerContext.Project)) throw new InvalidOperationException("The dependency target changed.");
            StartPackageDependencyRestore(project, service =>
            {
                if (selected.Loaded != null) service.AddLoadedDependency(project, selected.Loaded);
                else service.AddSuggested(project, selected.Suggested!);
            });
            selectedDependencyReference = null; selectedDependencyPackage = null; addingPackageDependency = null;
            packageDependencyPickerContext = null; selectedPackageDependency = null; packageDependencyCandidates = [];
            closePackageDependencyPicker = true;
        });
    }
}
