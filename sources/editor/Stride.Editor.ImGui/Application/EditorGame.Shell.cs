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
    private void InitializePanels()
    {
        var style = Gui.GetStyle();
        style.WindowPadding = new NVector2(12);
        style.FramePadding = new NVector2(8, 5);
        style.ItemSpacing = new NVector2(8, 6);
        style.FrameRounding = 3;
        var projectArg = Array.IndexOf(args, "--project");
        if (projectArg >= 0 && projectArg + 1 < args.Length) StartOpenProject(args[projectArg + 1]);
        else RestoreLastEditorSession();
        var testArg = Array.IndexOf(args, "--ui-test");
        if (testArg >= 0 && testArg + 1 < args.Length)
        {
            projectPathInput = WorkspaceRecoveryFixtureGuard.Resolve(args[testArg + 1], args);
            var engineCapabilityArgument = Array.IndexOf(args, "--engine-capability");
            if (engineCapabilityArgument >= 0)
            {
                if (engineCapabilityArgument + 2 >= args.Length) throw new ArgumentException("--engine-capability requires scenario and artifacts directory");
                var artifacts = args[engineCapabilityArgument + 2];
                engineAdditionalProbe = args[engineCapabilityArgument + 1] switch
                {
                    "E02" => new EnginePickingCapabilityProbe(this, artifacts),
                    "E02.gizmo" => new EngineGizmoCapabilityProbe(this, artifacts),
                    "E02.staleGeneration" => new EngineStalePickGenerationProbe(this, artifacts),
                    "E03.texture" => new EnginePreviewCapabilityProbe(this, artifacts, false),
                    "E03.model" => new EnginePreviewCapabilityProbe(this, artifacts, true),
                    "E03.animation" => new EnginePreviewFamilyProbe(this, artifacts, "animation"),
                    "E03.sprite" => new EnginePreviewFamilyProbe(this, artifacts, "sprite"),
                    "E03.font" => new EnginePreviewFamilyProbe(this, artifacts, "font"),
                    "E03.group" => new EnginePreviewGroupCapabilityProbe(this, artifacts),
                    "F.group" => new EngineParityBatchCapabilityProbe(this, artifacts),
                    "E07.settings" => new EngineSettingsUiCapabilityProbe(this, artifacts),
                    "E04" => new EngineRecompileCapabilityProbe(this, artifacts),
                    "E05" => new EngineMultiViewCapabilityProbe(this, artifacts),
                    "E05.groups" => new EngineDocumentGroupsCapabilityProbe(this, artifacts),
                    "E05.groups-mixed" => new EngineMixedDocumentGroupsCapabilityProbe(this, artifacts),
                    "E05.groups-layout" => new EngineDocumentGroupsCapabilityProbe(this, artifacts,
                        SaveDocumentGroupParityLayout, QueueDocumentGroupParityLayoutRestore),
                    "E08.recovery.write" => new EngineRecoveryWorkspaceCapabilityProbe(this, artifacts, true),
                    "E08.recovery.read" => new EngineRecoveryWorkspaceCapabilityProbe(this, artifacts, false),
                    "E09.aggregate-flags" => new EngineAggregateFlagsUiCapabilityProbe(this, artifacts),
                    "E06" => new EngineResourceLifecycleProbe(this, artifacts),
                    "E06.recovery" => new EngineDeviceRecoveryCapabilityProbe(this, artifacts),
                    _ => throw new ArgumentException("Unknown engine capability scenario"),
                };
                resetLayout = true;
                return;
            }
            var engineTestArgument = Array.IndexOf(args, "--engine-scene-test");
            if (engineTestArgument >= 0)
            {
                if (engineTestArgument + 1 >= args.Length) throw new ArgumentException("--engine-scene-test requires an artifacts directory");
                engineSceneProbe = new EngineSceneCapabilityProbe(this, args[engineTestArgument + 1]);
                resetLayout = true;
                return;
            }
            workflowTest = new UiWorkflowTest(this, args.Contains("--discard-close-test"), args.Contains("--plane-asset-test"), args.Contains("--picking-test"));
            var groupArgument = Array.IndexOf(args, "--ui-group");
            if (groupArgument >= 0)
            {
                if (groupArgument + 2 >= args.Length) throw new ArgumentException("--ui-group requires group ID and artifacts directory");
                workflowTest.ConfigureInspectorGroup(args[groupArgument + 1], args[groupArgument + 2]);
                resetLayout = true;
                return;
            }
            var scenarioArgument = Array.IndexOf(args, "--ui-scenario");
            if (scenarioArgument >= 0)
            {
                if (scenarioArgument + 1 >= args.Length) throw new ArgumentException("--ui-scenario requires a scenario ID");
                workflowTest.SelectScenario(args[scenarioArgument + 1] switch
                {
                    "U01" => UiWorkflowScenario.U01ProjectLoading,
                    "U01.failure" => UiWorkflowScenario.U01FailureRecovery,
                    "U02" => UiWorkflowScenario.U02Hierarchy,
                    "U03" => UiWorkflowScenario.U03Inspector,
                    "U03.references" => UiWorkflowScenario.U03ReferencesCollections,
                    "U03.numeric" => UiWorkflowScenario.U03NumericBoundaries,
                    "U06" => UiWorkflowScenario.U06MaterialEditor,
                    "U06.animation" => UiWorkflowScenario.U06AnimationEditor,
                    "U06.sprite" => UiWorkflowScenario.U06SpriteEditor,
                    "U06.ui" => UiWorkflowScenario.U06UIEditor,
                    "U06.compositor" => UiWorkflowScenario.U06CompositorEditor,
                    "U04" => UiWorkflowScenario.U04AssetBrowser,
                    "U07" => UiWorkflowScenario.U07Layout,
                    "U07.output" => UiWorkflowScenario.U07Output,
                    "U07.errors" => UiWorkflowScenario.U07ErrorNavigation,
                    "U07.layout-write" => UiWorkflowScenario.U07LayoutWrite,
                    "U07.layout-read" => UiWorkflowScenario.U07LayoutRead,
                    "U05" => UiWorkflowScenario.U05DocumentClose,
                    "U05SaveAll" => UiWorkflowScenario.U05SaveAll,
                    _ => throw new ArgumentException("Unknown UI capability scenario"),
                });
            }
            if (!IsWorkflowLayoutRestartRead) resetLayout = true;
        }
        if (UseUserPreferences && projectArg < 0 && projectLoading == null && workspace == null)
            ProjectCreation.Open();
    }

    private void DrawPanels()
    {
        RefreshVisualCaptureTitles();
        // Startup-only settings must also be applied after live code updates.
        Gui.GetStyle().WindowMenuButtonPosition = ImGuiDir.None;
        if (System.Reflection.Metadata.MetadataUpdater.IsSupported && !Window.Title.EndsWith(" [Hot Reload]", StringComparison.Ordinal))
            Window.Title += " [Hot Reload]";
        if (smokeTest) { DrawPrototypePanels(); return; }
        Controls.Centers.Clear();
        Controls.Viewports.Clear();
        BeginSpriteCanvasFrame();
        sceneViewportVisible = false;
        if (IsEditorGpuRetirementFinalizing)
        {
            ClearPropertyPathDropTargets();
            PumpRecoveryTransitions();
            PumpProjectLoading();
            PumpEditorClosing();
            Gui.DockSpaceOverViewport();
            DrawProjectLoading();
            return;
        }
        icons ??= new EditorIcons(GraphicsDevice, renderer!);
        engineViewport ??= new EngineViewport(this, renderer!);
        if (engineViewport.ResolveChildSceneDocument == null)
        {
            engineViewport.ResolveChildSceneDocument = ResolveSceneHierarchyDocument;
            engineViewport.ResolveSceneWorldOffset = GetSceneHierarchyOffset;
            engineViewport.SceneLoaded = IsSceneLoaded;
            engineViewport.SceneLocked = IsSceneLocked;
            engineViewport.EntityLocked = IsHierarchyEntityLocked;
            engineViewport.ScenePresentationVersion = () => scenePresentationVersion;
        }
        if (engineViewport.Gizmos != null && engineViewport.Gizmos.CanEditDocument == null)
        {
            engineViewport.Gizmos.ResolveRuntimeEntity = engineViewport.ResolveRuntimeEntity;
            engineViewport.Gizmos.CanEditDocument = owner => owner is not SceneDocument scene || IsSceneLoaded(scene) && !IsSceneLocked(scene);
            engineViewport.Gizmos.SelectionDocuments = () => SelectedHierarchyDocuments;
            engineViewport.Gizmos.SelectionPrimary = () => CompositeHierarchyPrimary;
            engineViewport.Gizmos.SelectionRoots = engineViewport.GetWorldTransformRoots;
            BindSceneRootTransformTargets(engineViewport.Gizmos, engineViewport);
            engineViewport.Gizmos.SelectionPolicyVersion = () => scenePresentationVersion;
            engineViewport.Gizmos.CanDuplicateSelectedEntities = () => CanDuplicateSceneGizmoSelection;
            engineViewport.Gizmos.DuplicateSelectedEntities = DuplicateSceneGizmoSelection;
        }
        PumpRecoveryTransitions();
        PumpProjectLoading();
        if (!RecoveryTransitionBusy && projectLoading == null && !IsAuthoringPipelineUpdating && !Gui.IsAnyItemActive()) TickRecovery();
        PumpPlatformUpdateViewRestores();
        if (!IsPackageDependencyBusy && !IsPackagePlatformUpdating) PumpAssetDocumentActivation();
        PumpEmptyWorkspaceViewRestores();
        PumpSceneEditorPreferences();
        PumpAssetPreviewUpdate();
        PumpEditorClosing();
        TickExternalAssetChanges();
        BindEditorComponentImages();
        Gui.BeginDisabled(RecoveryTransitionBusy || projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null);
        dockedPanels = 0;
        DrawMainMenu(icons);
        HandleEditorShortcuts();
        var dockspace = Gui.DockSpaceOverViewport();
        if (resetLayout && CanCreateInitialLayout() && CreateInitialLayout(dockspace)) resetLayout = false;

        BeginPropertyPathDropFrame();
        using (MeasureEditorFrameStage(EditorFrameStage.Hierarchy)) DrawHierarchyPanel(icons);
        DrawDocumentPanel(engineViewport);
        ProcessUIEditorRequests();
        ApplyDeliveredAssetDrop();
        using (MeasureEditorFrameStage(EditorFrameStage.Inspector)) DrawInspectorPanel();
        using (MeasureEditorFrameStage(EditorFrameStage.AssetBrowser)) DrawWorkspacePanel();
        DrawAssetReferences();
        DrawAssetErrors();
        DrawEditHistory();
        DrawCurveEditorPanel();
        DrawAssetPreview();
        DrawAssetEditor();
        PumpSpriteFileDrops();
        Gui.EndDisabled();
        DrawEditorExtensions();
        DrawRecoveryDialog();
        DrawExternalAssetChanges();
        DrawAssetRepair();
        DrawDebugWindow();
        DrawProjectLoading();
        DrawAssetPreviewUpdate();
        DrawTextureSettingsUpdate();
        DrawSpriteSheetUpdate();
        DrawPackageDependencyRestoreProgress();
        DrawOpenProjectDialog();
        DrawProjectCreationDialog();
        DrawAddedProjectCreationDialog();
        DrawAssetImportOptionsDialog();
        DrawAssetImportProgress();
        DrawOperationProgress();
        DrawDiscardDialog();
        DrawEditorCloseDialog();
        DrawDocumentCloseDialog();
        DrawEntityDeletionDialog();
        DrawEntityMovePopup();
        DrawAssetActionDialog();
        DrawAssetCreationDialog();
        DrawAssetDeletionDialog();
        DrawAssetFolderCreationDialog();
        DrawProjectLocationRemovalDialog();
        DrawPackageDependencyDialogs();
        DrawPackagePlatformUpdateDialog();
        DrawScriptSourceReloadPrompt();
        DrawPlatformDeploymentPrompt();
        DrawEditorSettings();
        DrawContentReferencePickerDialog();
        DrawAboutDialog();
        DrawSpriteSourceActionMessage();
        ObserveEditorSelection();
        if (showDemo) Gui.ShowDemoWindow(ref showDemo);
        mouseCursor?.Update(Window.NativeWindow.Handle, IsActive);
    }
}
