using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Core.Mathematics;
using Stride.Core.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame : Game
{
    protected override bool IsEditorGame => true;
    // Match EmbeddedGame: an editor host must not let the game's logger take over its console.
    protected override LogListener GetLogListener() => null!;
    protected override bool IsActiveForTick => ObserveEditorAudioFocus(base.IsActiveForTick || platformViewports?.IsEditorFocused == true) || IsScriptOleFeedbackTick;
    private readonly string[] args;
    private EngineSceneCapabilityProbe? engineSceneProbe;
    private EngineUiCapabilityProbe? engineAdditionalProbe;
    private bool IsEditorGraphicsUnavailable => GraphicsDevice == null || GraphicsDevice.IsPhysicallyUnavailable;
    private ImGuiContextPtr guiContext;
    private ImGuiRenderer? renderer;
    private WindowsClipboard? clipboard;
    private ImGuiImeInput? imeInput;
    private WindowsImGuiViewports? platformViewports;
    private readonly ImGuiInput guiInput = new();
    private WindowsMouseCursor? mouseCursor;
    private SmokeTestScene? preview;
    private bool showDemo;
    private bool animate = true;
    private int frames;
    private string scratchText = "Try keyboard input here";
    private readonly bool smokeTest;
    private readonly bool measureUiControls;
    private bool resetLayout;
    private NVector2 notesPosition;
    private int dockedPanels;
    private int initialViewportWidth;
    private readonly string settingsPath;

    public EditorGame(string[] args)
    {
        Services = new(message => statusMessage = message);
        this.args = args;
        settingsPath = EditorStartupPreferences.LayoutPath(args);
        var layoutArgument = Array.IndexOf(args, "--layout-file");
        if (layoutArgument >= 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        }
        Controls.Enabled = args.Contains("--ui-test") || args.Contains("--record-ui-controls");
        smokeTest = args.Contains("--smoke-test");
        measureUiControls = args.Contains("--measure-ui-controls");
        AutoLoadDefaultSettings = false;
        GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0, GraphicsProfile.Level_10_0];
        var initialSize = measureUiControls ? (Width: 1280, Height: 800)
            : WindowsWindowPlacement.DefaultBackBufferSize();
        GraphicsDeviceManager.PreferredBackBufferWidth = initialSize.Width;
        GraphicsDeviceManager.PreferredBackBufferHeight = initialSize.Height;
        if (args.Contains("--ui-test") || smokeTest)
        {
            GraphicsDeviceManager.SynchronizeWithVerticalRetrace = false;
            DrawWhileMinimized = true;
            TreatNotFocusedLikeMinimized = false;
        }
    }

    protected override unsafe void Initialize()
    {
        LoadEditorPreferences();
        diagnosticRouting = new EditorDiagnosticRouting(preferences?.Current ?? FallbackSceneEditorPreferences);
        InitializeEditorAudioFocus();
        base.Initialize();
        Services.Commands.DeviceFailure = error => EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
        ConfigureEditorShaderSources();
        Window.Title = "Stride — Dear ImGui editor experiment";
        Window.AllowUserResizing = true;
        Window.Closing += OnEditorWindowClosing;
        IsMouseVisible = true;
        guiContext = Gui.CreateContext();
        Gui.SetCurrentContext(guiContext);
        clipboard = new WindowsClipboard(Window.NativeWindow.Handle, isolate: args.Contains("--ui-test") || Controls.Enabled);
        if (!smokeTest) InitializeSpriteFileDrops();
        imeInput = new ImGuiImeInput(() => Input.TextInput);
        mouseCursor = new WindowsMouseCursor();
        InitializeMainWindowPlacement();
        Gui.GetIO().ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
        Gui.StyleColorsDark();
        Gui.GetStyle().WindowMenuButtonPosition = ImGuiDir.None;
        EditorFontSetup.Initialize();
        unsafe { var io = Gui.GetIO(); io.IniFilename = null; }
        InitializeScriptEditorFont();
        InitializePackagePropertiesFont();
        fontSourcePool = new();
        fontSourcePool.Canonicalize(Gui.GetIO().Fonts);
        InitializeVisualCapture();
        resetLayout = smokeTest || args.Contains("--measure-ui-controls") || !File.Exists(settingsPath);
        if (!resetLayout) resetLayout = !LoadEditorLayout(settingsPath);
        if (!smokeTest) InitializePanels();
        InitializeDpiScaling();
        var nativeDocumentGroupsTest = args.Contains("--native-document-groups-test");
        if (nativeDocumentGroupsTest && (!args.Contains("--ui-test") || !args.Contains("--layout-file") || !WindowsBackgroundLaunch.Enabled))
            throw new ArgumentException("Native document-group diagnostics require an isolated UI fixture, private layout and background launch.");
        if (!smokeTest && (!Controls.Enabled || nativeDocumentGroupsTest) && !measureUiControls && !args.Any(argument => argument.StartsWith("--capture", StringComparison.Ordinal)))
            platformViewports = new WindowsImGuiViewports(GraphicsDevice, Window.NativeWindow.Handle, message => statusMessage = message, ApplyViewportStyle, PlatformViewportPlacementFile, syntheticInput: nativeDocumentGroupsTest);
    }

    private string? PlatformViewportPlacementFile => !UseUserPreferences || Controls.Enabled
        ? Path.Combine(args.Contains("--layout-file") ? Path.GetDirectoryName(settingsPath)!
            : Path.Combine(AppContext.BaseDirectory, "diagnostics", "viewport-placements", Environment.ProcessId.ToString()), "viewport-placements.json")
        : null;

    protected override void Update(GameTime gameTime)
    {
        if (exitApproved) return;
        using var frameStageUpdate = BeginFrameStageUpdate();
        PumpNativeDialogCancellationTest();
        if (!smokeTest) PumpEditorExtensionCommands();
        if (!smokeTest) PumpSpriteSourceCleanup();
        if (!smokeTest)
            using (MeasureEditorFrameStage(EditorFrameStage.ThumbnailRetirement)) PumpRetiredAssetThumbnailServices();
        if (!smokeTest) PumpAnimationPreviewScrub();
        if (IsEditorGraphicsRecovering) return;
        PumpMainWindowPlacement();
        if (!IsEditorGpuRetirementFinalizing) base.Update(gameTime);
        Gui.SetCurrentContext(guiContext);
        imeInput?.UpdateFocus(IsActive);
        PumpEditorAudioFocus();
        if (!smokeTest) PumpExternalAssetPreview();
        PumpEditorGpuRetirement();
        PumpScriptGlyphImages();
        using (MeasureEditorFrameStage(EditorFrameStage.AuthoringMaintenance))
        {
            if (!smokeTest) PumpPackageDependencyRestore();
            if (!smokeTest) PumpPackageDependencyRemovalCheck();
            if (!smokeTest) PumpAddedSolutionProjects();
            if (!smokeTest) PumpPackagePlatformUpdate();
            if (!smokeTest) PumpPackageMetadataSave();
            if (!smokeTest) PumpCancelledProjectCreation();
            if (!smokeTest) PumpEditorExtensions();
            if (!smokeTest) PumpEffectLogs();
            if (!smokeTest) PumpProjectIdeOpen();
            if (!smokeTest) PumpUserProjectBuild();
            if (!smokeTest) PumpSessionAuthoringCleanup();
            if (!smokeTest) PumpDocumentCloseRequests();
            if (!smokeTest) PumpRetiredEditorPreviews();
            if (!smokeTest) PumpSceneDocumentViewDrain();
        }
        using (MeasureEditorFrameStage(EditorFrameStage.PreviewMaintenance))
        {
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.AssetImportPolling)) PumpAssetImportUpdate();
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.CompositorPreviewPolling)) PumpCompositorSettingsUpdate();
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.ImportPreviewPolling)) PumpImportSettingsUpdate();
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.TexturePreviewPolling)) PumpTextureSettingsUpdate();
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.MaterialPreviewPolling)) PumpMaterialSettingsUpdate();
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.SkyboxPreviewPolling)) PumpSkyboxSettingsUpdate();
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.FontPreviewPolling)) PumpFontSettingsUpdate();
            if (!smokeTest)
                using (MeasureEditorFrameStage(EditorFrameStage.ScriptSemanticPolling)) PumpScriptSemantics();
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.AnimatedPreviewPolling)) PumpAnimatedMediaPreviews();
            if (!smokeTest && !IsPackageDependencyBusy)
                using (MeasureEditorFrameStage(EditorFrameStage.CatalogPreviewPolling)) PumpCatalogPreviewUpdate();
            if (!smokeTest)
                using (MeasureEditorFrameStage(EditorFrameStage.VisualScriptPolling)) PumpVisualScriptCompilation();
        }
        if (!smokeTest && !IsEditorGpuRetirementFinalizing)
        {
            using var nativeSceneUpdateStage = MeasureEditorFrameStage(EditorFrameStage.NativeSceneUpdate);
            UpdateSceneDocumentViews(gameTime);
        }
        var backBuffer = GraphicsDevice.Presenter.BackBuffer;
        if (platformViewports != null)
            platformViewports.UpdateBeforeNewFrame(Input, (float)gameTime.Elapsed.TotalSeconds);
        else if (workflowTest == null && engineSceneProbe == null && engineAdditionalProbe == null)
        {
            guiInput.Update(Input, IsActive, backBuffer.Width, backBuffer.Height, (float)gameTime.Elapsed.TotalSeconds);
        }
        else
        {
            var io = Gui.GetIO();
            io.DisplaySize = new NVector2(backBuffer.Width, backBuffer.Height);
            io.DisplayFramebufferScale = NVector2.One;
            io.DeltaTime = Math.Max(0.001f, (float)gameTime.Elapsed.TotalSeconds);
        }
        if (smokeTest) UpdateSmokeTest();
        if (!DiagnosticCompletionActive) workflowTest?.Update();
        PumpDiagnosticCompletion();
        if (animate && preview != null) preview.Rotation.Y += (float)gameTime.Elapsed.TotalSeconds * 20;
    }

    protected override void EndDraw(bool present)
    {
        base.EndDraw(present);
        platformViewports?.PresentAfterFrame(present);
        PumpRetiredEditorPreviews(submittedBoundary: true);
        CompleteComponentImageRender();
        EndScriptGlyphImageFrame();
        PumpEditorGpuRetirement(submittedBoundary: true);
        WriteNativeWindowTestReport();
    }

    protected override void Draw(GameTime gameTime)
    {
        if (exitApproved) return;
        using var frameStageDraw = MeasureEditorFrameStage(EditorFrameStage.Draw);
        Gui.SetCurrentContext(guiContext);
        if (renderer == null)
        {
            renderer = new ImGuiRenderer(GraphicsDevice);
            InitializeNativeTabText();
        }
        if (smokeTest) preview ??= new SmokeTestScene(GraphicsDevice, renderer, GraphicsContext);
        UpdateDpiScaling();
        PrepareScriptOleFeedbackFrame();
        PumpRecoveryWorkspaceRestoreBeforeFrame();
        PumpRecoveryWorkspaceDrainCompletion();
        PumpDocumentGroupParityLayoutRestoreBeforeFrame();
        nativeDockSelectionRestore.PumpBeforeFrame();
        nativeDockSelectionRestore.Observe(Controls.Enabled, "before-NewFrame");
        Gui.NewFrame();
        ImGuiP.ErrorRecoveryStoreState(ref graphicsFrameScope); graphicsFrameOpen = true;
        BeginVisualCaptureFrame();
        if (!IsEditorGpuRetirementFinalizing) PumpScenePicking();
        BeginUiControlMeasurement();
        var uiMeasureStart = measureUiControls ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var uiAllocationStart = measureUiControls ? GC.GetAllocatedBytesForCurrentThread() : 0;
        ResetUIEditorVisibility();
        using (MeasureEditorFrameStage(EditorFrameStage.Panels)) DrawPanels();
        DrawNativeExtensionLifecycleVerification();
        PumpMatchedBenchmarkViewport();
        // Native docking only accepts windows submitted after their dockspace.
        DrawNativeInputInspection();
        DrawNativeCloseInspection();
        RecordDockTabInspection();
        var uiMeasureTicks = measureUiControls ? System.Diagnostics.Stopwatch.GetTimestamp() - uiMeasureStart : 0;
        var uiAllocatedBytes = measureUiControls ? GC.GetAllocatedBytesForCurrentThread() - uiAllocationStart : 0;
        Gui.Render();
        nativeDockSelectionRestore.Observe(Controls.Enabled, "after-Render");
        graphicsFrameOpen = false;
        PumpNativeTabTextFailure();
        InventoryVisualCaptureWindows();
        EndUiControlMeasurement(uiMeasureTicks, uiAllocatedBytes);
        if (!sceneViewportVisible) { engineViewport?.Gizmos?.Hide(); engineViewport?.Picker?.Cancel("Scene view hidden"); }
        if (!IsEditorGpuRetirementFinalizing)
        {
            using var sceneSynchronizationStage = MeasureEditorFrameStage(EditorFrameStage.SceneSynchronization);
            SynchronizeSceneDocument();
        }
        SceneSystem.Visible = false;
        if (DiagnosticRenderHiddenScenes && !smokeTest && !IsEditorGpuRetirementFinalizing)
            foreach (var view in LiveSceneDocumentViewports) view.Visible = true;
        PumpSceneLightProbeCommands();
        var beforeEngine = GraphicsDevice.FrameDrawCalls;
        var sceneDrawStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!IsEditorGpuRetirementFinalizing)
        {
            using var nativeSceneDrawStage = MeasureEditorFrameStage(EditorFrameStage.NativeSceneDraw);
            base.Draw(gameTime);
            if (smokeTest) preview!.Draw(GraphicsContext);
            else RenderSceneDocumentViews(gameTime);
        }
        engineDrawCalls = checked((int)(GraphicsDevice.FrameDrawCalls - beforeEngine));
        SceneDrawMeasurements.Record(sceneDrawStart, engineDrawCalls);
        var backBuffer = GraphicsDevice.Presenter.BackBuffer;
        if (!IsEditorGpuRetirementFinalizing)
        {
            texturePreview?.Render();
            spriteSheetPreview?.Render();
            fontPreview?.Render();
            RenderSpriteCanvases();
            modelPreview?.Render(gameTime);
            materialPreview?.Render(gameTime);
            skyboxPreview?.Render(gameTime);
            RenderCatalogAssetPreviews(gameTime);
            RenderExternalAssetPreview(gameTime);
            RenderAnimationPreview(gameTime);
            RenderUIEditor(gameTime);
            RenderAssetThumbnails();
        }
        GraphicsContext.CommandList.SetRenderTargetAndViewport(null, backBuffer);
        GraphicsContext.CommandList.Clear(backBuffer, new Color4(0.08f, 0.09f, 0.11f, 1));
        renderer.Render(GraphicsContext, Gui.GetDrawData());
        platformViewports?.RenderPlatformWindows(GraphicsContext, renderer, GraphicsDevice, backBuffer.Format);
        CompleteVisualCaptureFrame(backBuffer);
        RetainWorkflowRenderedCapture(backBuffer);
        SaveApprovedCloseDiagnosticCapture(backBuffer);
        frames++;
        if (engineAdditionalProbe != null)
        {
            engineAdditionalProbe.Update();
            if (engineAdditionalProbe.Finished)
            {
                BeginDiagnosticCompletion(engineAdditionalProbe.Passed, engineAdditionalProbe.Result, CompleteDiagnosticResult);
            }
        }
        if (engineSceneProbe != null)
        {
            engineSceneProbe.Update();
            if (engineSceneProbe.Finished)
            {
                BeginDiagnosticCompletion(engineSceneProbe.Passed, engineSceneProbe.Result, CompleteDiagnosticResult);
            }
        }
        if (captureCheckpointSuffix != null)
        {
            var suffix = captureCheckpointSuffix; captureCheckpointSuffix = null;
            var captureIndex = Array.IndexOf(args, "--capture");
            if (captureIndex >= 0 && captureIndex + 1 < args.Length)
            {
                using var file = File.Create(Path.ChangeExtension(args[captureIndex + 1], suffix));
                backBuffer.Save(GraphicsContext.CommandList, file, ImageFileType.Png);
            }
        }
        if (workflowTest?.Finished == true && !DiagnosticCompletionActive)
        {
            SaveWorkflowRenderedCapture(backBuffer);
            Console.WriteLine(workflowTest.Result);
            workflowTest.WriteResult();
            BeginDiagnosticCompletion(workflowTest.Passed, workflowTest.Result, CompleteDiagnosticResult);
        }
        if (smokeTest && frames == 120)
        {
            if (scratchText != "Smoke typed" || dockedPanels != 4 || preview.Target?.Width == initialViewportWidth)
                throw new InvalidOperationException($"Smoke test failed: text='{scratchText}', docked panels={dockedPanels}, viewport={preview.Target?.Width}, initial={initialViewportWidth}.");
            var captureIndex = Array.IndexOf(args, "--capture");
            if (captureIndex >= 0 && captureIndex + 1 < args.Length)
            {
                using var file = File.Create(args[captureIndex + 1]);
                backBuffer.Save(GraphicsContext.CommandList, file, ImageFileType.Png);
            }
            Console.WriteLine($"SMOKE PASS: {frames} frames; {Gui.GetDrawData().TotalVtxCount} UI vertices; 4 docked panels; text editing; resized viewport {preview.Target?.Width}x{preview.Target?.Height}.");
            Exit();
        }
    }

    protected override void Destroy()
    {
        EnsurePackageMetadataSaveRetired();
        ClearOperationProgress();
        EnsurePackagePlatformReadersRetired();
        CancelSceneCameraNavigation();
        EnsureSceneCpuRetirementCompleted();
        EnsureEditorGpuRetirementCompleted();
        EnsureInstalledFontGraphicsRetired();
        workflowRenderedCapture?.Dispose(); workflowRenderedCapture = null;
        DisposeNativeTabText();
        ReleaseDrainedScriptGlyphImages();
        DisposeNativeReadOnlyTextViews();
        ClearSceneRootDragState();
        sessionHistory?.Extensions?.DrainOwnedAssetEditors();
        CancelSceneLightProbeCommands();
        DisposeExternalAssetPreviewRegistry();
        StopProjectIdeOpen();
        StopActiveProjectSwitch();
        StopAddedSolutionProjects();
        StopCompositorSettingsUpdate();
        projectCreationPanel?.CancelAndRetireGeneration();
        addedProjectCreation?.CancelAndRetireGeneration();
        ClearMaterialShaderSettingsState();
        RestoreEditorAudioFocus();
        CloseCurveEditor();
        CloseUIEditor();
        StopAssetImportUpdate();
        StopImportSettingsUpdate();
        StopAnimatedMediaPreviews();
        CloseCatalogAssetPreviews(true);
        ShutdownVisualCapture();
        DisposeSpriteFileDrops();
        ClearSpriteSourceActions();
        FinishPackageDependencyRemovalCheckForShutdown();
        FinishPackageDependencyRestoreForShutdown();
        CloseSpriteSheetPreview();
        CloseFontPreview();
        ClearRetiredEditorPreviews();
        ClearTemplateImages();
        ClearAssetThumbnails();
        ClearPackagePropertiesState();
        StopScriptSemantics();
        StopTextureSettingsUpdate();
        StopProjectBuildOperations();
        outputLog.Dispose();
        diagnosticRouting?.Dispose(); diagnosticRouting = null;
        SaveMainWindowPlacement();
        SaveEditorPreferences();
        nativeRecentProjects?.Dispose(); nativeRecentProjects = null;
        nativeProjectStartup?.Dispose(); nativeProjectStartup = null;
        SaveSceneCameraSettings();
        if (assetPreviewBuild is { } retiringPreviewBuild)
        {
            RetireShutdownJob(retiringPreviewBuild, assetPreviewCancellation, ReleaseGraphicsRecoveryProjectOutput, "Preview build");
            assetPreviewBuild = null; assetPreviewCancellation = null; assetPreviewProgress = null;
        }
        if (projectLoading is { } retiringProjectLoad)
        {
            RetireShutdownJob(retiringProjectLoad, projectCancellation, loaded => loaded.ReleaseUnadopted(), "Project loading");
            projectLoading = null; projectCancellation = null; projectLoadProgress = null;
        }
        if (!guiContext.IsNull)
        {
            Gui.SetCurrentContext(guiContext);
            if (!resetLayout && !smokeTest && (workflowTest == null || workflowTest.PersistLayoutForRestart) && !args.Contains("--measure-ui-controls"))
            {
                SaveEditorLayout(settingsPath);
            }
            ClearDocumentSession();
            preview?.Dispose();
            engineViewport?.Dispose();
            StopSkyboxSettingsUpdate();
            texturePreview?.Dispose();
            modelPreview?.Dispose();
            materialPreview?.Dispose();
            ClearCompiledTextures();
            ClearRefreshedModels();
            ClearCompiledCompositors();
            ClearImportedContentOwners();
            ClearEditorGraphicsRecovery();
            projectContent?.Dispose();
            ClearRetainedActiveProjectContent();
            workspace?.Session.Dispose();
            icons?.Dispose();
            platformViewports?.Dispose(); platformViewports = null;
            mouseCursor?.Dispose(); mouseCursor = null;
            renderer?.Dispose();
            clipboard?.Dispose();
            imeInput?.Dispose();
            Gui.DestroyContext(guiContext);
            fontSourcePool?.ReleaseAfterContextDestroyed(); fontSourcePool = null;
        }
        base.Destroy();
    }
}

