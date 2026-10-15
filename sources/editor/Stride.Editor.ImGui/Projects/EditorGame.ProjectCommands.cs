using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRecentProjectsMenu()
    {
        var recent = PresentedRecentProjectMetadata();
        if (!BeginNativeMenu(EditorLocalization.Widget("Open recent", "Menu"), recent.Count > 0)) return;
        try
        {
            if (preferences != null)
            {
                string? open = null;
                foreach (var item in recent)
                {
                    var path = item.Path;
                    Gui.PushID(path);
                    try
                    {
                    if (DrawRecentProjectMenuRow(item)) open = path;
                    if (Gui.IsItemHovered())
                    {
                        Gui.BeginTooltip();
                        try { Gui.TextUnformatted(path); }
                        finally { Gui.EndTooltip(); }
                    }
                    }
                    finally { Gui.PopID(); }
                }
                if (open != null) RequestRecentProjectOpen(open);
            }
            Gui.Separator();
            if (DrawNativeMenuItem(EditorLocalization.Widget("Clear list", "Menu"))) ClearRecentProjects();
        }
        finally { Gui.EndMenu(); }
    }
    private void DrawProjectMenu()
    {
        var menuOpen = BeginNativeMenu(EditorLocalization.Widget("Project", "Menu"), workspace != null);
        if (Controls.Enabled) Controls.Record("menu/project");
        if (!menuOpen) return;
        try
        {
            if (workspace != null)
            {
                DrawProjectBuildCommands();
                Gui.Separator();
                DrawProjectLocationCommands();
                Gui.Separator();
                DrawSolutionProjectCommands();
                DrawProjectDependencyCommand();
                DrawPackagePlatformUpdateCommand();
                if (DrawNativeMenuItem(EditorLocalization.Widget("Package properties", "Menu"), "", false, SelectedPackagePropertiesTarget() != null))
                    Execute(RequestSelectedPackageProperties);
                if (BeginNativeMenu(EditorLocalization.Widget("Set as current project", "Menu")))
                {
                    try
                    {
                        foreach (var project in workspace.Projects)
                        {
                            Gui.PushID(project.Id.ToString());
                            try
                            {
                                if (DrawNativeMenuItem(Path.GetFileNameWithoutExtension(project.FullPath.ToOSPath()), "", ReferenceEquals(workspace.Session.CurrentProject, project))
                                    && !ReferenceEquals(workspace.Session.CurrentProject, project))
                                {
                                    Execute(() => StartActiveProjectSwitch(project));
                                }
                            }
                            finally { Gui.PopID(); }
                        }
                    }
                    finally { Gui.EndMenu(); }
                }
                if (DrawNativeMenuItem("Save package / solution changes", "", false, workspace.HasPackageChanges && !IsPackageMetadataSaving)) Execute(() => RequestPackageMetadataSave(saveDocuments: true));
                DrawOpenInIdeCommand();
                if (DrawNativeMenuItem(EditorLocalization.Widget("Reload project", "Menu"), "", false, !IsProjectLoading && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding)) RequestReloadProject();
                if (DrawNativeMenuItem("Repair missing source paths...", "", false, !IsAssetPreviewUpdating && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding)) RequestAssetRepair(true);
                if (DrawNativeMenuItem("Repair missing references...", "", false, !IsAssetPreviewUpdating && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding)) RequestAssetRepair(false);
                var closeProject = DrawNativeMenuItem("Close project");
                if (Controls.Enabled) Controls.Record("project/close");
                if (closeProject) RequestReplace(CloseProject, true, true);
            }
        }
        finally { Gui.EndMenu(); }
    }
    private void SavePackageChanges() => SavePackageChanges(true);
    private void SavePackageChanges(bool saveDocuments)
    {
        EnsurePackageMetadataSaveRetired();
        if (IsPackagePlatformUpdating) throw new InvalidOperationException("The package platforms are being updated.");
        if (IsPackageDependencyBusy) throw new InvalidOperationException("A project is already loading.");
        var expectedWorkspace = workspace ?? throw new InvalidOperationException("Open a project first.");
        var expectedHistory = SessionAuthoringHistory;
        void CheckSaveOwner()
        {
            if (!ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(SessionAuthoringHistory, expectedHistory)
                || IsPackageDependencyBusy || IsPackagePlatformUpdating)
                throw new InvalidOperationException("The project changed while saving pending edits.");
        }
        ValidatePendingPropertyStrings();
        CheckSaveOwner();
        if (expectedHistory is { } history)
            foreach (var project in expectedWorkspace.Projects.Where(project => !project.Package.IsReadOnly))
                GetProjectBuildSettingsDocument(project, history);
        if (saveDocuments) documents.SaveAll();
        CheckSaveOwner();
        SynchronizeSavedGameSettingsOrientations();
        ValidateRecoveredPackageBaselines();
        using (LockAndReconcileSavedAssetItems(expectedWorkspace, expectedHistory))
        using (assetDeletionHistory.LockPendingDeletionFiles()) expectedWorkspace.SavePackageChanges(RemoveOwnedAssetFile, SaveOwnedProjectMembership, SaveOwnedSolution, SaveOwnedPackageMetadata);
        CheckSaveOwner();
        FinalizeCutSourceDirectories();
        assetDeletionHistory.NotifySaved(); assetRelocationHistory.NotifySaved(); packageFolderHistory.NotifySaved();
        if (authoring != null) foreach (var state in authoring.PublishedStates)
            if (state.Document is PackageMetadataDocument metadata) metadata.NotifyNativeSessionSaved();
        if (!documents.HasDirtyDocuments && !workspace!.HasPackageChanges) sessionHistory?.NotifySaveAll(true);
        statusMessage = "Package / solution changes saved";
    }
    private bool SaveOwnedProjectMembership(SolutionProject project, AssetItem asset, bool adding)
    {
        if (project.Package.IsReadOnly) throw new IOException("This package can't be edited.");
        var state = SessionAuthoringHistory?.Registry.Find(project.FullPath.ToOSPath());
        if (state?.Document is not ProjectBuildSettingsDocument owner)
            throw new IOException("The project file has no canonical authored owner.");
        owner.ValidateProjectAssetMembership(asset, adding);
        if (owner.IsDirty) owner.Save();
        return true;
    }
    private void CloseProject()
    {
        var discardAuthorized = RecoveryTransitions.DiscardAuthorized;
        BeginRecoveryProtectedClose(() =>
        {
            if (discardAuthorized) RevertWorkspaceMetadata();
            CloseProjectCore();
        }, () => { }, discardAuthorized, ReportRecoveryTransitionFailure);
    }
    private void CloseProjectCore()
    {
        EnsurePackageMetadataSaveRetired();
        ClearOperationProgress();
        EnsurePackagePlatformReadersRetired();
        EnsureEditorGpuRetirementCompleted();
        ClearProjectLocationRemoval();
        sessionHistory?.Extensions?.DrainOwnedAssetEditors();
        StopProjectIdeOpen();
        StopActiveProjectSwitch();
        StopAddedSolutionProjects();
        StopCompositorSettingsUpdate();
        ClearAssetImportOptionsDialog();
        ClearAssetCreationState();
        ClearMaterialShaderSettingsState();
        CloseCurveEditor();
        CloseUIEditor();
        StopAssetImportUpdate();
        StopImportSettingsUpdate();
        StopAnimatedMediaPreviews();
        CloseCatalogAssetPreviews(true);
        ClearPackageDependencyRestoreState();
        CloseSpriteSheetPreview();
        CloseFontPreview();
        ClearRetiredEditorPreviews();
        ClearAssetThumbnails();
        ClearPackagePropertiesState();
        StopScriptSemantics();
        StopTextureSettingsUpdate();
        StopProjectBuildOperations();
        SaveSceneCameraSettings();
        texturePreview?.Dispose(); texturePreview = null;
        modelPreview?.Dispose(); modelPreview = null;
        materialPreview?.Dispose(); materialPreview = null;
        ClearSolutionExplorerWorkspace();
        engineViewport?.ClearScene(); ClearRefreshedModels(); ClearDocumentSession();
        StopSkyboxSettingsUpdate();
        ClearCompiledTextures();
        ClearCompiledCompositors();
        ClearImportedContentOwners();
        projectContent?.Dispose(); projectContent = null;
        ClearRetainedActiveProjectContent();
        workspace?.Session.Dispose(); workspace = null;
        assetBrowser.Bind(null); assetPreviewPath = null;
        ClearAssetActions();
        ClearAssetFolderCreation(); packageFolderHistory.Clear(); ClearEditorSelectionHistory();
        ClearAssetTagState();
        ClearAssetReferenceState();
        selectedAssetPath = null; hierarchyFilter = assetFilter = "";
        inspectedEntity = null; componentErrors.Clear(); activeColorPopup = null;
        statusMessage = "Project closed";
    }
}
