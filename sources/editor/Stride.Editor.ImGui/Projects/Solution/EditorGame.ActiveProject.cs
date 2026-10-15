using Stride.Core.Assets;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Task<string>? activeProjectBuild;
    private CancellationTokenSource? activeProjectCancellation;
    private sealed record ActiveProjectSwitchTarget(ProjectWorkspace Workspace, SolutionProject Project, string Path);
    private ActiveProjectSwitchTarget? activeProjectTarget;
    private ProjectLoadState? activeProjectLoadState;
    private ProjectLoadProgressCapture? activeProjectProgress;
    private readonly List<CompiledProjectContent> retainedActiveProjectContent = [];
    internal bool IsActiveProjectSwitching => activeProjectBuild != null;

    private void StartActiveProjectSwitch(SolutionProject project)
    {
        if (workspace == null || !workspace.Session.Projects.Contains(project)) throw new InvalidOperationException("The project is not in this session.");
        if (project.Package.State < PackageState.AssetsReady) throw new InvalidOperationException("The project has not loaded successfully.");
        if (ReferenceEquals(workspace.Session.CurrentProject, project)) return;
        if (IsActiveProjectSwitching || IsProjectLoading || IsAddingSolutionProjects || IsAuthoringPipelineUpdating || IsUserProjectBuilding
            || assetPreviewBuild != null || RecoveryTransitionBusy) throw new InvalidOperationException("A project operation is already running.");
        var expectedWorkspace = workspace;
        var expectedPath = project.FullPath.ToOSPath();
        ValidatePendingPropertyStrings();
        if (!ReferenceEquals(workspace, expectedWorkspace) || !expectedWorkspace.Session.Projects.Contains(project)
            || !SamePath(project.FullPath.ToOSPath(), expectedPath) || project.Package.State < PackageState.AssetsReady
            || IsActiveProjectSwitching || IsProjectLoading || IsAddingSolutionProjects || IsAuthoringPipelineUpdating
            || IsUserProjectBuilding || assetPreviewBuild != null || RecoveryTransitionBusy)
            throw new InvalidOperationException("The selected project changed while finishing pending edits.");
        if (ReferenceEquals(expectedWorkspace.Session.CurrentProject, project)) return;
        activeProjectTarget = new(expectedWorkspace, project, expectedPath);
        activeProjectCancellation = new(); var cancellation = activeProjectCancellation.Token;
        activeProjectLoadState = new("Building project", Path.GetFileNameWithoutExtension(project.FullPath.ToOSPath()));
        var progress = new ProjectLoadProgressCapture(); activeProjectProgress = progress;
        var path = expectedPath;
        activeProjectBuild = Task.Run(() => CompiledProjectBuild.Build(path, cancellation,
            state =>
            {
                progress.Append(state);
                if (ReferenceEquals(Volatile.Read(ref activeProjectProgress), progress)) Volatile.Write(ref activeProjectLoadState, state);
                RecordBuildOutput(state);
            }), cancellation);
    }
    private void PumpActiveProjectSwitch()
    {
        if (activeProjectBuild?.IsCompleted != true) return;
        var task = activeProjectBuild; var target = activeProjectTarget;
        var expected = target?.Workspace; var project = target?.Project;
        var cancelled = activeProjectCancellation!.IsCancellationRequested;
        activeProjectBuild = null; activeProjectProgress = null; activeProjectTarget = null;
        activeProjectCancellation.Dispose(); activeProjectCancellation = null;
        if (cancelled || task.IsCanceled)
        {
            if (task.Status == TaskStatus.RanToCompletion) RetireShutdownJob(task, null, ReleaseGraphicsRecoveryProjectOutput, "Cancelled project switch");
            else if (task.IsFaulted) _ = task.Exception;
            statusMessage = "Project switch cancelled"; return;
        }
        Execute(() =>
        {
            var output = task.GetAwaiter().GetResult();
            if (!ReferenceEquals(workspace, expected) || project == null || target == null
                || !workspace!.Session.Projects.Contains(project) || !SamePath(project.FullPath.ToOSPath(), target.Path)
                || project.Package.State < PackageState.AssetsReady)
            {
                RetireShutdownJob(Task.FromResult(output), null, ReleaseGraphicsRecoveryProjectOutput, "Unadopted project switch");
                throw new InvalidOperationException("The project session changed while preparing the active project.");
            }
            CompiledProjectContent next;
            try { next = new CompiledProjectContent(this, output, () => ReleaseGraphicsRecoveryProjectOutput(output)); }
            catch
            {
                RetireShutdownJob(Task.FromResult(output), null, ReleaseGraphicsRecoveryProjectOutput, "Failed project switch mount");
                throw;
            }
            var previousActivation = workspace.CaptureActivation();
            var previousProject = previousActivation.Project;
            var previousContent = projectContent;
            var previousPreference = preferences?.Current.ActiveProjects.GetValueOrDefault(workspace.OpenPath);
            var installed = false;
            try
            {
                SaveSceneCameraSettings();
                workspace.SetActiveProject(project, output);
                if (projectContent != null) retainedActiveProjectContent.Add(projectContent);
                projectContent = next;
                installed = true;
                if (preferences != null) { preferences.Current.ActiveProjects[workspace.OpenPath] = project.FullPath.ToOSPath(); preferences.Save(); }
                SelectBuildTarget(project, false);
                StopScriptSemantics();
                ClearAssetReferenceState();
                statusMessage = "Active project: " + Path.GetFileNameWithoutExtension(project.FullPath.ToOSPath());
            }
            catch (Exception failure)
            {
                try
                {
                if (installed)
                {
                    workspace.RestoreActivation(previousActivation);
                    projectContent = previousContent;
                    if (previousContent != null) retainedActiveProjectContent.Remove(previousContent);
                    if (preferences != null)
                    {
                        if (previousPreference == null) preferences.Current.ActiveProjects.Remove(workspace.OpenPath);
                        else preferences.Current.ActiveProjects[workspace.OpenPath] = previousPreference;
                        try { preferences.Save(); }
                        catch (Exception recovery) { throw new AggregateException("Active project selection and preference restoration failed.", failure, recovery); }
                    }
                    SelectBuildTarget(previousProject, false);
                }
                }
                finally { RetireEditorPreview(next); }
                throw;
            }
        });
    }
    private void DrawActiveProjectSwitchLoading()
    {
        if (!IsActiveProjectSwitching) return;
        DrawProjectLoadProgress(Volatile.Read(ref activeProjectLoadState), activeProjectProgress, activeProjectCancellation, "Building project", false);
    }
    private void StopActiveProjectSwitch()
    {
        var task = activeProjectBuild; var cancellation = activeProjectCancellation;
        activeProjectBuild = null; activeProjectProgress = null; activeProjectCancellation = null; activeProjectTarget = null;
        if (task == null) { cancellation?.Dispose(); return; }
        RetireShutdownJob(task, cancellation, ReleaseGraphicsRecoveryProjectOutput, "Project switch build");
    }
    private void ClearRetainedActiveProjectContent()
    {
        while (retainedActiveProjectContent.Count > 0)
        { retainedActiveProjectContent[0].Dispose(); retainedActiveProjectContent.RemoveAt(0); }
    }
}
