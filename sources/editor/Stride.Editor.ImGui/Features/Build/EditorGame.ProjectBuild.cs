using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ProjectWorkspace? buildWorkspace;
    private SolutionProject[] buildTargets = [];
    private SolutionProject? buildTarget;
    private DesktopProjectMetadata? buildMetadata;
    private Task<DesktopProjectMetadata>? buildMetadataTask;
    private CancellationTokenSource? buildMetadataCancellation;
    private Task<DesktopProjectBuildResult>? userProjectBuild;
    private CancellationTokenSource? userProjectBuildCancellation;
    private string buildConfiguration = "", buildFramework = "";
    private bool runAfterUserBuild;
    private bool IsUserProjectBuilding => userProjectBuild != null || scriptRecompileTask != null || IsPlatformDeploymentUpdating;
    private bool CanStartUserProjectBuild => workspace != null && ReferenceEquals(buildWorkspace, workspace)
        && buildTarget != null && buildMetadata != null
        && !IsUserProjectBuilding && projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating
        && !closeRequested && !closePromptOpen && !exitApproved;
    private bool CanRunUserProject => buildTarget?.Type == ProjectType.Executable
        && buildTarget.Platform is PlatformType.Windows or PlatformType.Android or PlatformType.Linux or PlatformType.macOS
        && buildMetadata?.Executable == true;

    private void PumpUserProjectBuild()
    {
        if (!ReferenceEquals(buildWorkspace, workspace))
        {
            StopProjectBuildOperations(); buildWorkspace = workspace;
            buildTargets = workspace?.Projects.Where(project => !project.Package.IsReadOnly && File.Exists(project.FullPath.ToOSPath())).ToArray() ?? [];
            var saved = workspace == null ? null : preferences?.Current.BuildTargetPaths.GetValueOrDefault(workspace.OpenPath);
            SelectBuildTarget(buildTargets.FirstOrDefault(project => string.Equals(project.FullPath.ToOSPath(), saved, StringComparison.OrdinalIgnoreCase))
                ?? buildTargets.FirstOrDefault(project => project.Type == ProjectType.Executable && project.Platform == PlatformType.Windows)
                ?? buildTargets.FirstOrDefault(project => string.Equals(project.FullPath.ToOSPath(), workspace?.BuildProjectPath, StringComparison.OrdinalIgnoreCase))
                ?? buildTargets.FirstOrDefault(), false);
        }
        PumpScriptSourceReload();
        PumpScriptAssemblyReload();
        PumpLiveScripting();
        PumpPlatformDeployment();
        if (buildMetadataTask?.IsCompleted == true)
        {
            var task = buildMetadataTask; buildMetadataTask = null;
            buildMetadataCancellation?.Dispose(); buildMetadataCancellation = null;
            Execute(() =>
            {
                buildMetadata = task.GetAwaiter().GetResult();
                var saved = preferences?.Current.BuildConfigurations.GetValueOrDefault(workspace!.OpenPath);
                buildConfiguration = buildMetadata.Configurations.FirstOrDefault(value => value == saved) ?? buildMetadata.Configurations[0];
                buildFramework = buildMetadata.Frameworks.FirstOrDefault() ?? "";
            });
        }
        if (userProjectBuild?.IsCompleted != true) return;
        var completed = userProjectBuild; userProjectBuild = null;
        var cancelled = userProjectBuildCancellation!.IsCancellationRequested;
        userProjectBuildCancellation.Dispose(); userProjectBuildCancellation = null;
        if (cancelled || completed.IsCanceled)
        {
            if (completed.IsFaulted) _ = completed.Exception;
            runAfterUserBuild = false;
            liveAfterUserBuild = false;
            statusMessage = "Build cancelled";
            outputLog.Append("Build", LogMessageType.Info, "Build", statusMessage); return;
        }
        try
        {
            var result = completed.GetAwaiter().GetResult();
            QueueScriptAssemblyReload(result);
            statusMessage = "Build succeeded";
            outputLog.Append("Build", LogMessageType.Info, "Build", statusMessage);
            if (liveAfterUserBuild && !closeRequested && !closePromptOpen && !exitApproved)
                pendingLiveScriptStart = result;
            if (runAfterUserBuild && !closeRequested && !closePromptOpen && !exitApproved)
            {
                var id = DesktopProjectBuild.Start(result);
                statusMessage = "Project started";
                outputLog.Append("Build", LogMessageType.Info, "Build", "Started " + Path.GetFileName(result.ProjectPath) + " (" + id + ").");
            }
        }
        catch (Exception error)
        {
            statusMessage = "Build failed";
            outputLog.Append("Build", LogMessageType.Error, "Build", error.ToString());
            Console.Error.WriteLine(error);
        }
        finally { runAfterUserBuild = false; liveAfterUserBuild = false; }
    }

    private void SelectBuildTarget(SolutionProject? project, bool remember)
    {
        DetachBuildTask(buildMetadataTask, buildMetadataCancellation);
        buildMetadataCancellation = null;
        buildMetadataTask = null; buildMetadata = null; buildTarget = project; buildConfiguration = ""; buildFramework = "";
        if (project == null) return;
        buildMetadataCancellation = new();
        buildMetadataTask = DesktopProjectBuild.ReadMetadata(project.FullPath.ToOSPath(), buildMetadataCancellation.Token);
        if (remember && workspace != null && preferences != null)
        { preferences.Current.BuildTargetPaths[workspace.OpenPath] = project.FullPath.ToOSPath(); SaveEditorPreferences(); }
    }

    private void DrawProjectBuildCommands()
    {
        using (ImGuiScopes.Disabled(IsUserProjectBuilding || projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating))
        {
            Gui.SetNextItemWidth(UiScale(240));
            using (var projects = ImGuiScopes.Combo("Project", buildTarget?.Name ?? ""))
            if (projects.DrawContents)
            {
                foreach (var target in buildTargets)
                {
                    Gui.PushID(target.FullPath.ToOSPath());
                    try { if (Gui.Selectable(target.Name, ReferenceEquals(target, buildTarget))) SelectBuildTarget(target, true); }
                    finally { Gui.PopID(); }
                }
            }
            using var metadata = ImGuiScopes.Disabled(buildMetadata == null);
            Gui.SetNextItemWidth(UiScale(160));
            using (var configurations = ImGuiScopes.Combo("Configuration", buildConfiguration))
            if (configurations.DrawContents)
            {
                foreach (var configuration in buildMetadata?.Configurations ?? [])
                    if (Gui.Selectable(configuration, buildConfiguration == configuration))
                    {
                        buildConfiguration = configuration;
                        if (workspace != null && preferences != null)
                        { preferences.Current.BuildConfigurations[workspace.OpenPath] = configuration; SaveEditorPreferences(); }
                    }
            }
            if (buildMetadata?.Frameworks.Length > 1)
            {
                Gui.SetNextItemWidth(UiScale(180));
                using var frameworks = ImGuiScopes.Combo("Framework", buildFramework);
                if (frameworks.DrawContents)
                {
                    foreach (var framework in buildMetadata.Frameworks)
                        if (Gui.Selectable(framework, buildFramework == framework)) buildFramework = framework;
                }
            }
            if (DrawNativeMenuItem(EditorLocalization.Widget("Build project", "Menu"), "F6", false, CanStartUserProjectBuild)) Execute(() => StartUserProjectBuild(false));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Start project", "Menu"), "F5", false, CanStartUserProjectBuild && CanRunUserProject)) Execute(() => StartUserProjectBuild(true));
            if (DrawNativeMenuItem(EditorLocalization.Widget("Start live-scripting", "Menu"), "", false, CanStartUserProjectBuild && CanRunUserProject && buildTarget?.Platform == PlatformType.Windows && !IsLiveScripting)) Execute(StartLiveScriptingBuild);
        }
        if (DrawNativeMenuItem(EditorLocalization.Widget("Cancel build", "Menu"), "Ctrl+F6", false, IsUserProjectBuilding)) CancelUserProjectBuild();
        if (DrawNativeMenuItem("Stop", "", false, IsLiveScripting)) StopLiveScripting();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reload game assemblies", "Menu"), "", false, pendingSourceReloadProjects.Count != 0 && !IsUserProjectBuilding))
        { sourceReloadPromptOpen = false; acceptedSourceReloadRevision = sourceReloadRevision; }
    }

    private void StartUserProjectBuild(bool run)
    {
        if (!CanStartUserProjectBuild) return;
        if (run && buildTarget?.Platform != PlatformType.Windows) { StartPlatformProjectRun(); return; }
        if (run && !CanRunUserProject)
            throw new InvalidOperationException("The selected project is not a Windows executable.");
        SaveForUserProjectBuild(request =>
        {
        userProjectBuildCancellation = new(); runAfterUserBuild = run; focusOutput = true;
        outputLog.Append("Build", LogMessageType.Info, "Build", "Building " + request.Name + " (" + request.Configuration + ").");
        statusMessage = "Build in progress…";
        var platform = request.Platform switch
        {
            PlatformType.Android => "Android", PlatformType.iOS => "iPhone", PlatformType.Linux => "Linux", PlatformType.macOS => "macOS", _ => ""
        };
        userProjectBuild = request.Platform == PlatformType.Android
            ? DesktopProjectBuild.BuildAndInstallAndroid(request.Project, request.Configuration, request.Framework,
                request.Solution, userProjectBuildCancellation.Token, RecordBuildOutput)
            : DesktopProjectBuild.Build(request.Project, request.Configuration, request.Framework,
                request.Solution, userProjectBuildCancellation.Token, RecordBuildOutput, platform);
        });
    }

    private sealed record UserProjectBuildRequest(string Name, string Project, string Configuration,
        string Framework, string Solution, PlatformType Platform);

    private void SaveForUserProjectBuild(Action<UserProjectBuildRequest> completed)
    {
        if (!CanStartUserProjectBuild || workspace is not { } owner || buildTarget is not { } target || buildMetadata is not { } metadata)
            throw new InvalidOperationException("The selected project is no longer ready to build.");
        var request = new UserProjectBuildRequest(target.Name, target.FullPath.ToOSPath(), buildConfiguration,
            buildFramework, owner.OpenPath, target.Platform);
        SaveAllDocuments(() =>
        {
        if (!CanStartUserProjectBuild || !ReferenceEquals(workspace, owner) || !ReferenceEquals(buildTarget, target)
            || !ReferenceEquals(buildMetadata, metadata) || !owner.Projects.Contains(target)
            || !SamePath(target.FullPath.ToOSPath(), request.Project) || !SamePath(owner.OpenPath, request.Solution)
            || target.Platform != request.Platform || buildConfiguration != request.Configuration || buildFramework != request.Framework)
            throw new InvalidOperationException("The selected project changed while saving pending edits.");
        completed(request);
        }, error =>
        {
            runAfterUserBuild = liveAfterUserBuild = false;
            statusMessage = "Save failed: " + error.Message;
            outputLog.Append("Build", LogMessageType.Error, "Build", error.Message, error.ToString());
        });
    }

    private void CancelUserProjectBuild()
    {
        userProjectBuildCancellation?.Cancel(); scriptRecompileCancellation?.Cancel(); platformRunCancellation?.Cancel();
        if (remoteCredentialsOpen) StopPlatformDeployment();
    }

    private void StopProjectBuildOperations()
    {
        StopLiveScripting();
        StopPlatformDeployment();
        StopScriptAssemblyReload();
        StopScriptSourceReload();
        DetachBuildTask(userProjectBuild, userProjectBuildCancellation);
        DetachBuildTask(buildMetadataTask, buildMetadataCancellation);
        userProjectBuild = null; userProjectBuildCancellation = null;
        buildMetadataTask = null; buildMetadataCancellation = null;
        runAfterUserBuild = false;
    }

    private static void DetachBuildTask(Task? task, CancellationTokenSource? cancellation)
    {
        cancellation?.Cancel();
        if (task == null) { cancellation?.Dispose(); return; }
        _ = task.ContinueWith(completed =>
        {
            if (completed.IsFaulted) _ = completed.Exception;
            cancellation?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
