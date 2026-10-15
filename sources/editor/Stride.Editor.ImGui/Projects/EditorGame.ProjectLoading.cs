using Hexa.NET.ImGui;
using Stride.Engine;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using SVector3 = Stride.Core.Mathematics.Vector3;
using SQuaternion = Stride.Core.Mathematics.Quaternion;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string projectPathInput = "";
    private Task<ProjectWorkspace>? projectLoading;
    private CancellationTokenSource? projectCancellation;
    private ProjectLoadState? projectLoadState;
    private ProjectLoadProgressCapture? projectLoadProgress;
    private long projectLoadCleanupRetryAt;
    private string? projectLoadCleanupFailure;
    private Task<PreparedNativeMigration>? nativeMigrationPreparation;
    private TaskCompletionSource<bool>? nativeMigrationPublication;
    private NativeUpgradePrompt? nativeUpgradePrompt;
    private bool nativeMigrationPublicationRequested;
    private ProjectReloadViews? projectReloadViews;
    internal bool IsProjectLoading => projectLoading != null;
    internal int? ProjectBuildProcessId => Volatile.Read(ref projectLoadState)?.ProcessId;
    private bool showOpenProject;
    private void DrawOpenProjectDialog()
    {
        if (showOpenProject) { Gui.OpenPopup("Open project"); showOpenProject = false; }
        Gui.SetNextWindowSize(UiScale(650, 190), ImGuiCond.Appearing);
        using var modal = ImGuiScopes.Modal("Open project", ImGuiWindowFlags.AlwaysAutoResize);
        TraceWorkflowUiAction("modal/project-open", modal.DrawContents);
        if (!modal.DrawContents) return;
        Gui.SetNextItemWidth(UiScale(480)); NativeStringInput.Draw("##PackagePath", ref projectPathInput);
        if (Controls.Enabled) Controls.Record("project/path");
        Gui.SameLine();
        if (DrawNativeButton("Browse...")) Execute(() =>
        {
            var directory = ReadOpenProjectDialogDirectory();
            var selected = WindowsFileDialogs.Open(Window.NativeWindow.Handle, "Open Stride project or solution",
                "Stride projects and solutions\0*.sdpkg;*.csproj;*.sln;*.slnx\0Solutions\0*.sln;*.slnx\0Stride packages\0*.sdpkg\0C# projects\0*.csproj\0All files\0*.*\0\0", directory, projectPathInput);
            if (selected != null) { projectPathInput = selected; RememberProjectPath(selected); }
        });
        var openProjectAccepted = DrawNativeButton(EditorLocalization.Widget("Open", "Menu"));
        TraceWorkflowUiAction("project/open", openProjectAccepted);
        if (openProjectAccepted) { var path = projectPathInput; Gui.CloseCurrentPopup(); RequestReplace(() => StartOpenProject(path), true, true); }
        if (Controls.Enabled) Controls.Record("project/open");
        Gui.SameLine(); if (DrawNativeButton(EditorLocalization.Widget("Cancel", "Button"))) Gui.CloseCurrentPopup();
    }
    private void StartOpenProject(string path, string? selectedProject = null, ProjectReloadViews? reloadViews = null)
    {
        if (projectLoading != null) throw new InvalidOperationException("A project is already loading.");
        if (IsAuthoringPipelineUpdating || assetPreviewBuild != null || IsUserProjectBuilding) throw new InvalidOperationException("A build is already running.");
        ValidatePendingPropertyStrings();
        selectedProject ??= preferences?.Current.ActiveProjects.FirstOrDefault(pair => string.Equals(pair.Key, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).Value;
        projectLoadCleanupRetryAt = 0; projectLoadCleanupFailure = null;
        projectCancellation = new CancellationTokenSource();
        projectReloadViews = reloadViews;
        var token = projectCancellation.Token;
        var progressCapture = new ProjectLoadProgressCapture();
        projectLoadProgress = progressCapture;
        void ReportLoad(ProjectLoadState state) => ReportProjectLoadState(state, progressCapture);
        projectLoadState = new ProjectLoadState("Opening project", Path.GetFileName(path));
        nativeUpgradePrompt = new NativeUpgradePrompt();
        nativeMigrationPublication = new(TaskCreationOptions.RunContinuationsAsynchronously);
        nativeMigrationPublicationRequested = false;
        var prompt = nativeUpgradePrompt;
        var publication = nativeMigrationPublication;
        var preparation = new TaskCompletionSource<PreparedNativeMigration>(TaskCreationOptions.RunContinuationsAsynchronously);
        nativeMigrationPreparation = preparation.Task;
        projectLoading = Task.Run(async () =>
        {
            try { return new ProjectWorkspace(path, token, ReportLoad, selectedProject); }
            catch (NativeMigrationRequiredException) { }
            token.ThrowIfCancellationRequested();
            ReportLoad(new ProjectLoadState("Preparing package upgrade", "Preparing native migration in a private project copy..."));
            using var migration = PreparedNativeMigration.Prepare(path, prompt, token);
            preparation.TrySetResult(migration);
            await publication.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new ProjectWorkspace(path, token, ReportLoad, selectedProject);
        });
        statusMessage = "Loading project and compiling its content...";
    }
    private void PumpProjectLoading()
    {
        PumpActiveProjectSwitch();
        PumpProjectLocationRemoval();
        PumpNativeMigrationPublication();
        if (projectLoading?.IsCompleted != true) return;
        var task = projectLoading;
        var cancelled = projectCancellation!.IsCancellationRequested;
        if (cancelled || task.IsCanceled)
        {
            if (!TryCleanupEditorOwnedWork(() =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.ReleaseUnadopted();
                else if (task.IsFaulted) _ = task.Exception;
            }, ref projectLoadCleanupRetryAt, ref projectLoadCleanupFailure, "Project loading cleanup")) return;
            projectLoading = null; projectLoadProgress = null;
            ClearNativeMigrationLoadingState();
            projectCancellation.Dispose(); projectCancellation = null;
            AbortRecoveryProjectReplacement();
            projectReloadViews = null;
            statusMessage = "Project loading cancelled";
            return;
        }
        projectLoading = null; projectLoadProgress = null;
        ClearNativeMigrationLoadingState();
        projectCancellation.Dispose(); projectCancellation = null;
        Execute(() =>
        {
            ProjectWorkspace loaded;
            try { loaded = task.GetAwaiter().GetResult(); }
            catch { projectReloadViews = null; AbortRecoveryProjectReplacement(); throw; }
            if (workspace != null && projectContent != null && !loaded.HasCompiledContent
                && loaded.CompilationFailure is { } replacementCompileFailure)
            {
                // Loading can return an authored package after a failed compile.
                // A usable prior compiled session must survive that result; it is
                // not an adoptable replacement and owns no retirement barrier.
                projectReloadViews = null;
                AbortRecoveryProjectReplacement();
                try { loaded.ReleaseUnadopted(); }
                catch (Exception cleanupError)
                {
                    EditorBackgroundJobRetirement.Retire(Task.FromResult(loaded), null,
                        failed => failed.ReleaseUnadopted(), "Rejected project replacement");
                    outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error,
                        "Replacement cleanup", cleanupError.ToString());
                }
                throw new InvalidOperationException("Replacement project compilation failed; the current compiled project remains open.",
                    replacementCompileFailure);
            }
            var adopted = false;
            BeginRecoveryProjectAdoption(() =>
            {
            if (RecoveryTransitions.DiscardAuthorized) RevertWorkspaceMetadata();
            if (preferences != null)
            {
                if (loaded.Session.CurrentProject != null) preferences.Current.ActiveProjects[loaded.OpenPath] = loaded.BuildProjectPath;
                else preferences.Current.ActiveProjects.Remove(loaded.OpenPath);
            }
            RememberOpenedProject(projectPathInput = loaded.OpenPath);
            SaveSceneCameraSettings();
            StopAddedSolutionProjects();
            StopCompositorSettingsUpdate();
            ClearAssetImportOptionsDialog();
            CloseSpriteSheetPreview();
            CloseFontPreview();
            CloseCatalogAssetPreviews(true);
            CloseCurveEditor();
            CloseUIEditor();
            StopAssetImportUpdate();
            StopImportSettingsUpdate();
            StopAnimatedMediaPreviews();
            ClearRetiredEditorPreviews();
            ClearAssetThumbnails();
            ClearPackagePropertiesState();
            texturePreview?.Dispose(); texturePreview = null;
            modelPreview?.Dispose(); modelPreview = null;
            materialPreview?.Dispose(); materialPreview = null;
            engineViewport?.ClearScene();
            StopSkyboxSettingsUpdate();
            ClearCompiledTextures();
            ClearProjectLocationRemoval();
            ClearSolutionExplorerWorkspace();
            ClearRefreshedModels(); ClearDocumentSession();
            ClearCompiledCompositors();
            ClearImportedContentOwners();
            projectContent?.Dispose(); projectContent = null;
            ClearRetainedActiveProjectContent();
            workspace?.Session.Dispose();
            StopScriptSemantics();
            workspace = loaded;
            ClearAssetCreationState();
            ClearMaterialShaderSettingsState();
            adopted = true;
            InitializeSessionAuthoring(loaded);
            InitializeEditorExtensions(loaded);
            assetBrowser.Bind(loaded); assetPreviewPath = null; ClearAssetActions();
            InitializeEffectLogs();
            ClearAssetFolderCreation(); packageFolderHistory.Clear(); ClearEditorSelectionHistory();
            ClearAssetTagState();
            ClearAssetReferenceState();
            selectedAssetPath = null;
            if (loaded.HasCompiledContent)
            {
                var ownedOutput = loaded.CompiledOutput;
                projectContent = new CompiledProjectContent(this, ownedOutput, () => OwnedProjectBuildOutput.Release(ownedOutput));
            }
            else if (loaded.CompilationFailure is { } compileError)
                outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Compilation", compileError.ToString());
            var defaultScene = workspace.Package is { } activePackage
                ? Stride.Assets.GameSettingsAssetExtensions.GetGameSettingsAssetOrDefault(activePackage).DefaultScene : null;
            var reference = defaultScene == null ? null : Stride.Core.Serialization.AttachedReferenceManager.GetAttachedReference(defaultScene);
            var scene = workspace.HasActivePackage
                ? workspace.Assets.FirstOrDefault(asset => asset.Extension == ".sdscene" && reference != null && asset.Id == reference.Id)
                    ?? workspace.Assets.FirstOrDefault(asset => asset.Extension == ".sdscene" && asset.Name == Stride.Assets.GameSettingsAsset.DefaultSceneLocation)
                    ?? workspace.Assets.FirstOrDefault(asset => asset.Extension == ".sdscene")
                : null;
            if (scene != null) OpenScene(scene.Path);
            else { document = null; statusMessage = "Package loaded; no scene assets"; }
            if (projectReloadViews is { } reopen) { projectReloadViews = null; RestoreProjectReloadViews(reopen); }
            }, () => { if (!adopted) loaded.ReleaseUnadopted(); });
        });
    }
    private void DrawProjectLoading()
    {
        DrawActiveProjectSwitchLoading();
        if (projectLoading == null) return;
        DrawProjectLoadProgress(Volatile.Read(ref projectLoadState), projectLoadProgress, projectCancellation, "Opening project", true);
        nativeUpgradePrompt?.Draw();
    }
    private void PumpNativeMigrationPublication()
    {
        if (projectLoading == null || projectCancellation == null) return;
        if (nativeMigrationPublicationRequested || nativeMigrationPreparation?.IsCompletedSuccessfully != true) return;
        nativeMigrationPublicationRequested = true;
        var plan = nativeMigrationPreparation.Result;
        var publication = nativeMigrationPublication!;
        var cancellation = projectCancellation!.Token;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            NativeMigrationPublication.Validate(plan);
            if (plan.Changes.Length == 0) { publication.TrySetResult(true); return; }
            BeginRecoveryProjectAdoption(() =>
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    NativeMigrationPublication.Validate(plan);
                    if (RecoveryTransitions.DiscardAuthorized) RevertWorkspaceMetadata();
                    CloseProjectCore();
                    var result = NativeMigrationPublication.Publish(plan, () => workspace == null && SessionAuthoringHistory == null);
                    statusMessage = $"Upgraded {result.ChangedFiles} project files";
                    publication.TrySetResult(true);
                }
                catch (Exception error) { publication.TrySetException(error); throw; }
            }, () => publication.TrySetException(new IOException("Recovery checkpoint prevented native migration publication.")));
        }
        catch (Exception error) { publication.TrySetException(error); }
    }
    private void ClearNativeMigrationLoadingState()
    {
        nativeMigrationPreparation = null; nativeMigrationPublication = null;
        nativeUpgradePrompt?.Dispose(); nativeUpgradePrompt = null;
        nativeMigrationPublicationRequested = false;
    }
}
