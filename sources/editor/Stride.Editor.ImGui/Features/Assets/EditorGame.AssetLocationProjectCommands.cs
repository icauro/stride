using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanSwitchExplorerProject(SolutionProject project)
        => workspace != null && workspace.Session.Projects.Contains(project)
            && ReferenceEquals(project.Package.Container, project) && ReferenceEquals(project.Package.Session, workspace.Session)
            && IsCurrentEditableAssetPackage(project.Package)
            && !IsActiveProjectSwitching && !IsProjectLoading && !IsAddingSolutionProjects && !IsPackageDependencyBusy
            && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && assetPreviewBuild == null && !RecoveryTransitionBusy
            && !closeRequested && !closePromptOpen && closingDocument == null && !editorExtensionsStopping;

    private bool CanOpenExplorerIde => workspace?.Session.SolutionPath is { } solution && File.Exists(solution.ToOSPath())
        && projectIdeOpen == null && !IsProjectLoading && !IsAddingSolutionProjects && !RecoveryTransitionBusy
        && !closeRequested && !closePromptOpen && closingDocument == null && !editorExtensionsStopping;

    private Action? DrawAssetLocationProjectActions(AssetBrowserLocation location)
    {
        if (workspace is not { } owner || !ReferenceEquals(assetBrowser.FindLocation(location.Id), location)) return null;
        var version = assetBrowser.SelectionVersion;
        bool Current() => ReferenceEquals(workspace, owner) && assetBrowser.SelectionVersion == version
            && ReferenceEquals(assetBrowser.FindLocation(location.Id), location);
        Action? action = null;
        if (location.Folder is { Parent: null } root && root.Package.Container is SolutionProject project
            && IsCurrentEditableAssetPackage(root.Package) && owner.Session.Projects.Contains(project))
        {
            var projectPath = project.FullPath.ToOSPath();
            if (DrawNativeMenuItem(EditorLocalization.Widget("Set as current project", "Menu"), "", false, CanSwitchExplorerProject(project)))
                action = () =>
                {
                    if (Current() && CanSwitchExplorerProject(project) && ReferenceEquals(root.Package.Container, project)
                        && SamePath(project.FullPath.ToOSPath(), projectPath)) StartActiveProjectSwitch(project);
                };
        }
        if (owner.Session.SolutionPath is { } solution && !string.IsNullOrWhiteSpace(solution.ToOSPath()))
        {
            var solutionPath = solution.ToOSPath();
            if (DrawNativeMenuItem(EditorLocalization.Widget("New project...", "Menu"), "", false, CanRequestSolutionProject))
                action = () =>
                {
                    if (Current() && IsCurrentSolutionProjectRequest(owner, solutionPath)) RequestNewSolutionProject();
                };
            if (DrawNativeMenuItem(EditorLocalization.Widget("Add existing project...", "Menu"), "", false, CanRequestSolutionProject))
                action = () =>
                {
                    if (Current() && IsCurrentSolutionProjectRequest(owner, solutionPath)) RequestAddExistingSolutionProject();
                };
            if (DrawNativeMenuItem(EditorLocalization.Widget("Open in IDE", "Menu"), "", false, CanOpenExplorerIde))
                action = () =>
                {
                    if (Current() && CanOpenExplorerIde && owner.Session.SolutionPath is { } current
                        && SamePath(current.ToOSPath(), solutionPath)) StartProjectIdeOpen();
                };
        }
        return action;
    }
}
