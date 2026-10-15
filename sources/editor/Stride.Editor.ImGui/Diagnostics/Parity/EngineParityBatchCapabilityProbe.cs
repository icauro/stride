using System.Text.Json;

namespace Stride.Editor.ImGui;

/// <summary>One disposable loaded package, with explicit service/native case evidence and terminal retirement certification.</summary>
internal sealed class EngineParityBatchCapabilityProbe : EngineUiCapabilityProbe
{
    private const string Coverage = "Service/native capability proof; not UI E2E or physical input.";
    private readonly List<CaseReceipt> receipts = [];
    private readonly HashSet<string> selectedFamilies;
    private static readonly string[] DefaultFamilies = ["shell", "project", "save", "model", "inspector", "sprite", "authority", "texture"];
    private static readonly string[] AllFamilies = [.. DefaultFamilies, "audio", "compositor", "inputs", "thumbnails", "dependencies", "library-dependencies", "visualscript", "stock-dispatch", "asset-templates", "extensions"];
    private bool templateFixturePrepared, visualScriptBaselinePrepared;
    private object? visualScriptBaselineArrangement, visualScriptLegacyBaselineArrangement;
    private EngineAssetTemplateCapabilityProbe? assetTemplates;
    private EngineFbxTemplateCapabilityProbe? fbxTemplates;
    private string[] templateLocations = [];
    private int templateLocationIndex;
    private bool extensionLifecycleStarted;
    private bool stockDispatchFixturePrepared;
    private EngineStockDispatchCapabilityProbe? stockDispatch;
    private EngineStockAssetDispatchCapabilityProbe? stockPrefabDispatch, stockLibraryDispatch;
    private EngineStockVisualScriptSaveCapabilityProbe? stockVisualScriptDispatch;
    private bool stockVisualScriptPrepared;
    private bool visualScriptFixturePrepared;
    private EngineVisualScriptHistoryCapabilityProbe? visualScriptHistory;
    private object? workspaceOwner, historyOwner, compiledOwner, spriteFixture;
    private InspectorParityScriptFixture? scriptFixture;
    private EditorGame.ProjectSaveParityRun? saveRun;
    private ThemePreferencesParityRun? themeRun;
    private EditorGame.MixedSaveParityRun? mixedSaveRun;
    private EditorGame.PackageDiskConflictParityRun? packageDiskConflictRun;
    private EngineUiCanvasCapabilityProbe? uiCanvas;
    private EngineAssetReimportCapabilityProbe? reimport;
    private bool uiCanvasFixturePrepared;
    private EditorGame.RecoveryIntegrityParityRun? recoveryIntegrityRun;
    private bool inheritanceVerificationStarted;
    private ActiveProjectParityRun? activeProjectRun;
    private EngineActiveProjectPendingUiCapabilityProbe? activeProjectPending;
    private EngineScriptCompletionUiCapabilityProbe? scriptCompletionUi;
    private CompilerStartRetirementParityRun? compilerStartRun;
    private ActiveProjectStalePathParityRun? staleProjectPathRun;
    private ProjectFolderReadFailureParityRun? projectFolderReadFailureRun;
    private EditorGame.AssetCrossVolumeRelocationParityRun? crossVolumeRelocationRun;
    private bool activeProjectFixturePrepared;
    private Exception? saveFailure;
    private int spriteIndex;
    private string? currentCase;
    private AudioParityFixture? audioFixture;
    private bool compositorFixturePrepared;
    private bool inputsVerified;
    private Task<object>? thumbnailIdleRun;
    private readonly CancellationTokenSource thumbnailIdleCancellation = new();
    private bool thumbnailIdleVerified;
    private Task<object>? dependencyRestoreRun;
    private bool dependencyRestoreVerified;
    private bool dependencySolutionPrepared;
    private Task<object>? libraryDependencyRun;
    private bool libraryDependencyVerified;
    private string? audioRuntimeUrl;
    private string[] plannedCases = [];
    private sealed record CaseReceipt(string Id, string Family, bool Passed, object Evidence);
    protected override string Scenario => "F.group";
    protected override double PhaseTimeoutSeconds => Phase == 40 ? 180
        : Phase <= 2 ? selectedFamilies.Overlaps(["dependencies", "library-dependencies"]) ? 300 : 180 : 90;
    protected override object Evidence => new
    {
        coverage = Coverage, selectedFamilies, plannedCases, currentCase, completedCases = receipts.Count,
        sameWorkspace = workspaceOwner != null && ReferenceEquals(workspaceOwner, Game.VerificationWorkspaceOwner),
        sameCompiledContent = compiledOwner != null && ReferenceEquals(compiledOwner, Game.VerificationCompiledProjectOwner),
        spriteFixture, audioFixture, audioRuntimeUrl,
        visualScriptHistory = visualScriptHistory?.CapabilityEvidence,
        stockDispatch = stockDispatch?.CapabilityEvidence,
        stockPrefabDispatch = stockPrefabDispatch?.CapabilityEvidence,
        stockLibraryDispatch = stockLibraryDispatch?.CapabilityEvidence,
        stockVisualScriptDispatch = stockVisualScriptDispatch?.CapabilityEvidence,
        activeProjectPending = activeProjectPending?.CaptureEvidence(),
        projectFolderReadFailure = projectFolderReadFailureRun == null ? null : Game.CaptureProjectFolderReadFailureParity(projectFolderReadFailureRun),
        crossVolumeRelocation = crossVolumeRelocationRun?.Evidence,
        scriptCompletionUi = scriptCompletionUi?.CapabilityEvidence,
        sceneUiSplit = Game.SceneUiGroupParityProgress,
        visualScriptBaselineArrangement, visualScriptLegacyBaselineArrangement,
        assetTemplates = assetTemplates?.CapabilityEvidence,
        templateLocations, templateLocationIndex,
        thumbnailCleanupError = Game.NativeThumbnailIdleVerificationDrainError,
        dependencyCleanupError = Game.P02DependencyRestoreParityCleanupError,
        libraryDependencyCleanupError = Game.P02NugetLibraryParityCleanupError,
        cases = receipts.Select(item => new { caseId = item.Id, family = item.Family, scenarioPassed = item.Passed }),
        terminalTeardown = "Case results are provisional until the actual outer host terminal retirement succeeds."
    };
    internal EngineParityBatchCapabilityProbe(EditorGame game, string artifacts) : base(game, artifacts)
    {
        SharedTerminalTeardownPath = Path.Combine(Artifacts, "teardown-result.json");
        var arguments = Environment.GetCommandLineArgs();
        var selector = Array.IndexOf(arguments, "--parity-batches");
        if (selector >= 0 && selector + 1 >= arguments.Length)
            throw new ArgumentException("--parity-batches requires a comma-delimited family list.");
        selectedFamilies = new(selector < 0 ? DefaultFamilies
            : arguments[selector + 1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        if (selectedFamilies.Count == 0 || selectedFamilies.Any(family => !AllFamilies.Contains(family, StringComparer.Ordinal)))
            throw new ArgumentException("Unknown or empty parity family selection. Supported families: " + string.Join(",", AllFamilies));
    }
    protected override void Step()
    {
        if (saveFailure != null)
        {
            if (saveRun != null && !Game.RetireProjectSaveParityVerification(saveRun)) return;
            saveRun = null;
            Finish(false, saveFailure.ToString());
            return;
        }
        try { StepCore(); }
        catch (Exception error) when (saveRun != null)
        {
            saveFailure = error;
            var receipt = new CaseReceipt("save.metadata-replacement", "save", false, new { error = error.ToString() });
            receipts.Add(receipt); WriteCase(receipt, false, false);
            // The next owner frames retire the real detached producer before
            // reporting completion; never dispose a snapshot held by a worker.
        }
    }
    private void StepCore()
    {
        if (dependencyRestoreRun != null) Game.AdvanceP02DependencyRestoreParity();
        if (libraryDependencyRun != null) Game.AdvanceP02NugetLibraryParity();
        switch (Phase)
        {
            case 0:
                if (selectedFamilies.Contains("project") && !activeProjectFixturePrepared)
                {
                    Game.PrepareActiveProjectParityFixture(); activeProjectFixturePrepared = true;
                }
                if (plannedCases.Length == 0) WritePlan();
                if (selectedFamilies.Contains("asset-templates") && !templateFixturePrepared)
                {
                    Game.PrepareAssetTemplateReferenceFixture(Game.ParityFixturePackagePath);
                    Game.PrepareFbxTemplateFixtureBeforeLoad(Game.ParityFixturePackagePath);
                    Game.PrepareAssetReimportFixtureBeforeLoad(Game.ParityFixturePackagePath);
                    templateFixturePrepared = true;
                }
                if (selectedFamilies.Contains("stock-dispatch") && !stockDispatchFixturePrepared)
                {
                    Game.PrepareStockDispatchFixtureBeforeLoad();
                    stockDispatchFixturePrepared = true;
                }
                if (selectedFamilies.Overlaps(["visualscript", "stock-dispatch"]) && !visualScriptFixturePrepared)
                {
                    Game.PrepareVisualScriptHistoryFixtureBeforeLoad();
                    if (selectedFamilies.Contains("visualscript")) Game.PrepareVisualScriptLegacyReferencesFixtureBeforeLoad();
                    visualScriptFixturePrepared = true;
                }
                if (selectedFamilies.Overlaps(["dependencies", "library-dependencies"]) && !dependencySolutionPrepared)
                {
                    Game.PrepareP02DependencySolutionFixture();
                    dependencySolutionPrepared = true;
                }
                if (selectedFamilies.Contains("compositor") && !compositorFixturePrepared)
                {
                    Game.PrepareTwoViewCompositorParityFixture();
                    Game.PrepareCompositorSlotParityFixture(Game.ParityFixturePackagePath);
                    compositorFixturePrepared = true;
                }
                if (selectedFamilies.Contains("authority") && !uiCanvasFixturePrepared)
                { Game.PrepareUiCanvasParityFixture(); uiCanvasFixturePrepared = true; }
                if (selectedFamilies.Contains("audio") && audioFixture == null)
                    audioFixture = Game.PrepareAudioParityFixture();
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented()) break;
                Game.AssertCompiledSceneVerification();
                Require(!Game.Document.IsDirty, "The parity batch must begin with a clean actual scene.");
                if (selectedFamilies.Contains("inputs") && !inputsVerified)
                {
                    currentCase = "inputs.polling-certificate";
                    if (!Game.StepNativePollingInputsParityVerification()) break;
                    Record(currentCase, "inputs", new { certificate = Game.PollingCertificateParityVerificationEvidence,
                        importPolling = Game.ImportPollingParityVerificationEvidence });
                    inputsVerified = true;
                }
                if (selectedFamilies.Contains("thumbnails") && !thumbnailIdleVerified)
                {
                    currentCase = "thumbnails.idle-restart";
                    thumbnailIdleRun ??= Game.VerifyNativeThumbnailIdleParityAsync(Path.Combine(Artifacts, "thumbnails"), thumbnailIdleCancellation.Token);
                    if (!thumbnailIdleRun.IsCompleted) break;
                    Record(currentCase, "thumbnails", thumbnailIdleRun.GetAwaiter().GetResult());
                    thumbnailIdleVerified = true;
                }
                if (selectedFamilies.Contains("dependencies") && !dependencyRestoreVerified)
                {
                    currentCase = "dependencies.restore";
                    dependencyRestoreRun ??= Game.StartP02DependencyRestoreParity();
                    if (!dependencyRestoreRun.IsCompleted) break;
                    Record(currentCase, "dependencies", dependencyRestoreRun.GetAwaiter().GetResult());
                    dependencyRestoreVerified = true;
                }
                if (selectedFamilies.Contains("library-dependencies") && !libraryDependencyVerified)
                {
                    currentCase = "library-dependencies.restore";
                    libraryDependencyRun ??= Game.StartP02NugetLibraryParity();
                    if (!libraryDependencyRun.IsCompleted) break;
                    Record(currentCase, "library-dependencies", libraryDependencyRun.GetAwaiter().GetResult());
                    libraryDependencyVerified = true;
                }
                if (visualScriptFixturePrepared && !visualScriptBaselinePrepared)
                {
                    visualScriptBaselineArrangement = Game.PrepareParityVisualScriptBaseline();
                    visualScriptLegacyBaselineArrangement = Game.PrepareParityVisualScriptLegacyBaseline();
                    visualScriptBaselinePrepared = true; stockVisualScriptPrepared = true;
                }
                workspaceOwner = Game.VerificationWorkspaceOwner; historyOwner = Game.VerificationHistoryOwner;
                compiledOwner = Game.VerificationCompiledProjectOwner; Next(); break;
            case 3:
                if (!selectedFamilies.Contains("shell")) { Next(); break; }
                RequireSharedOwners();
                Run("shell.contract", "shell", () => Game.VerifyShellParityBatch(Path.Combine(Artifacts, "shell")));
                themeRun = Game.BeginThemePreferencesParityVerification(Path.Combine(Artifacts, "theme-preferences"));
                SeekPhase(30); break;
            case 30:
                RequireSharedOwners(); currentCase = "shell.theme-preferences";
                var themeEvidence = Game.StepThemePreferencesParityVerification(themeRun!);
                if (themeEvidence == null) break;
                Game.EndThemePreferencesParityVerification(themeRun!); themeRun = null;
                Record(currentCase, "shell", themeEvidence); SeekPhase(4); break;
            case 4:
                if (!selectedFamilies.Contains("project")) { Next(); break; }
                RequireSharedOwners();
                Run("project.dependencies", "project", () => Game.VerifyProjectParityDependencies(Path.Combine(Artifacts, "project")));
                Run("project.recent-projects", "project", () => Game.VerifyRecentProjectsParity(Path.Combine(Artifacts, "recent-projects")));
                activeProjectRun = Game.BeginActiveProjectParityVerification(); SeekPhase(35); break;
            case 35:
                currentCase = "project.active-project-switch";
                Require(ReferenceEquals(workspaceOwner, Game.VerificationWorkspaceOwner)
                    && ReferenceEquals(historyOwner, Game.VerificationHistoryOwner),
                    "Active project switching must preserve the actual workspace and native history owners.");
                var activeProjectEvidence = Game.StepActiveProjectParityVerification(activeProjectRun!);
                if (activeProjectEvidence == null) break;
                Require(ReferenceEquals(Game.VerificationCompiledProjectOwner, activeProjectRun!.ReturnedContent),
                    "The returned compiler bundle must be the actual production adopted owner.");
                compiledOwner = Game.VerificationCompiledProjectOwner;
                Record(currentCase, "project", activeProjectEvidence); activeProjectRun = null; SeekPhase(40); break;
            case 40:
                currentCase = "project.pending-switch";
                Require(ReferenceEquals(workspaceOwner, Game.VerificationWorkspaceOwner)
                    && ReferenceEquals(historyOwner, Game.VerificationHistoryOwner),
                    "Pending-edit project switching must retain the real workspace and history owners.");
                activeProjectPending ??= new(Game, Path.Combine(Artifacts, "active-project-pending"))
                    { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                activeProjectPending.Update();
                if (!activeProjectPending.Finished) break;
                Require(activeProjectPending.Passed, activeProjectPending.Result);
                Require(ReferenceEquals(Game.VerificationCompiledProjectOwner, activeProjectPending.ReturnedContent),
                    "Pending-edit project verification must return the genuinely adopted original project bundle.");
                compiledOwner = Game.VerificationCompiledProjectOwner;
                Record(currentCase, "project", activeProjectPending.CaptureEvidence());
                compilerStartRun = Game.BeginCompilerStartRetirementParityVerification(); SeekPhase(42); break;
            case 42:
                RequireSharedOwners(); currentCase = "project.compiler-start-retirement";
                var compilerStartEvidence = Game.StepCompilerStartRetirementParityVerification(compilerStartRun!);
                if (compilerStartEvidence == null) break;
                Record(currentCase, "project", compilerStartEvidence); compilerStartRun = null;
                staleProjectPathRun = Game.BeginActiveProjectStalePathParityVerification(); SeekPhase(43); break;
            case 43:
                RequireSharedOwners(); currentCase = "project.stale-target-path";
                var staleProjectPathEvidence = Game.StepActiveProjectStalePathParityVerification(staleProjectPathRun!);
                if (staleProjectPathEvidence == null) break;
                Record(currentCase, "project", staleProjectPathEvidence); staleProjectPathRun = null;
                projectFolderReadFailureRun = Game.BeginProjectFolderReadFailureParityVerification(Artifacts); SeekPhase(45); break;
            case 45:
                RequireSharedOwners(); currentCase = "project.folder-file-read-failure";
                var projectFolderReadEvidence = Game.StepProjectFolderReadFailureParityVerification(projectFolderReadFailureRun!);
                if (projectFolderReadEvidence == null) break;
                Record(currentCase, "project", projectFolderReadEvidence); projectFolderReadFailureRun = null; SeekPhase(5); break;
            case 5:
                if (!selectedFamilies.Contains("model")) { SeekPhase(13); break; }
                RequireSharedOwners(); currentCase = "model.batch";
                var history = Game.RunModelParityHistory(Path.Combine(Artifacts, "model-parity-" + Guid.NewGuid().ToString("N")));
                var geometry = Game.GenerateModelParityMeshes(history);
                Require(history.Length == ModelParityCases.All.Count && geometry.Length == history.Length,
                    "Every planned model property requires actual history/persistence and generated GPU geometry.");
                foreach (var result in history)
                {
                    var mesh = geometry.Single(item => item.Id == result.Id);
                    Require(result.HistoryVerified && result.PersistenceVerified, "Model case did not certify native history/persistence: " + result.Id);
                    Record(result.Id, "model", new { history = result, geometry = mesh });
                }
                SeekPhase(13); break;
            case 6:
                if (!selectedFamilies.Contains("inspector")) { SeekPhase(8); break; }
                RequireSharedOwners();
                Run("inspector.contract", "inspector", () => new
                { contracts = InspectorParityContracts.Run(), inventoryDiagnosticOnly = Game.CaptureInspectorParityVerification() });
                Run("inspector.progress", "inspector", InspectorParityProgressContracts.Run);
                scriptFixture = Game.PrepareScriptPresentationAuthorityVerification();
                currentCase = "inspector.script-authority"; Next(); break;
            case 7:
                RequireSharedOwners();
                if (!Game.IsScriptPresentationAuthorityVerificationReady(scriptFixture!)) break;
                Run("inspector.script-authority", "inspector", () => Game.RunScriptPresentationAuthorityVerification(scriptFixture!.Source));
                Game.CloseScriptPresentationAuthorityVerification(scriptFixture!); scriptFixture = null;
                Game.StartDictionaryContractVerification(); SeekPhase(28); break;
            case 28:
                RequireSharedOwners(); currentCase = "inspector.dictionary-sealed";
                var dictionaryEvidence = Game.StepDictionaryContractVerification();
                if (dictionaryEvidence == null) break;
                Record(currentCase, "inspector", dictionaryEvidence);
                Run("inspector.aggregate-collection-atomicity", "inspector", Game.VerifyAggregateCollectionParity);
                Run("inspector.nested-collection-stale-target", "inspector", Game.VerifyCollectionContextParity);
                Run("inspector.property-clipboard-ownership", "inspector", Game.VerifyPropertyClipboardParity);
                Run("inspector.automatic-preference-safety", "inspector", Game.VerifyAutomaticPreferenceSafety);
                Run("inspector.polymorphic-object-lifecycle", "inspector", Game.VerifyPolymorphicObjectParity);
                Run("inspector.polymorphic-external-alias-preservation", "inspector", Game.VerifyPolymorphicExternalAliasParity);
                SeekPhase(8); break;
            case 8:
                if (!selectedFamilies.Contains("sprite")) { SeekPhase(10); break; }
                RequireSharedOwners(); currentCase = "sprite.prepare";
                spriteFixture = Game.PrepareSpriteParityFixture(Path.Combine(Artifacts, "sprite"));
                Next(); break;
            case 9:
                RequireSharedOwners();
                if (!Game.SpriteParityCanvasReady) break;
                if (spriteIndex == 0 && !Game.SpriteParityNativeCompilerReady) break;
                var id = SpriteParityCases.Cases[spriteIndex];
                Run(id, "sprite", () =>
                {
                    var result = Game.RunSpriteParityCase(id, Path.Combine(Artifacts, "sprite", id));
                    Require(result.BaselineRestored && result.Checks.Length != 0,
                        "Sprite case must preserve the native saved fixture baseline: " + id);
                    return result;
                });
                if (++spriteIndex == SpriteParityCases.Cases.Count)
                { Game.CloseSpriteParityFixture(); Next(); }
                break;
            case 10:
                if (!selectedFamilies.Contains("authority")) { SeekPhase(12); break; }
                RequireSharedOwners(); currentCase = "authority.context";
                if (!Game.StepAuthorityParityVerification()) break;
                Record(currentCase, "authority", Game.AuthorityParityVerificationEvidence!); Next(); break;
            case 11:
                RequireSharedOwners(); currentCase = "authority.resize";
                if (!Game.StepAuthorityResizeVerification()) break;
                Record(currentCase, "authority", Game.AuthorityResizeVerificationEvidence!);
                SeekPhase(32); break;
            case 32:
                RequireSharedOwners(); currentCase = "authority.ui-canvas-ownership";
                uiCanvas ??= new(Game, Path.Combine(Artifacts, "ui-canvas"))
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                uiCanvas.Update();
                if (!uiCanvas.Finished) break;
                Require(uiCanvas.Passed, uiCanvas.Result);
                Record(currentCase, "authority", uiCanvas.CapabilityEvidence); SeekPhase(34); break;
            case 34:
                RequireSharedOwners(); currentCase = "authority.restore-scene-tab";
                if (Game.Document == null) break;
                if (!Game.VerifyRecoveryAcceptanceNativeTab(Game.Document.Path, out _))
                {
                    if (Game.RecordVerificationSceneDockTab()) Click("engine/scene-tab");
                    break;
                }
                if (!EnsureScenePresented()) break;
                SeekPhase(36); break;
            case 36:
                RequireSharedOwners(); currentCase = "authority.scene-ui-split-ownership";
                if (!Game.StepSceneUiGroupParity(control => Click(control))) break;
                Record(currentCase, "authority", Game.SceneUiGroupParityEvidence!);
                SeekPhase(12); break;
            case 12:
                RequireSharedOwners(); currentCase = "texture.batch";
                if (selectedFamilies.Contains("compositor"))
                {
                    currentCase = "compositor.refresh-certificate";
                    if (!Game.StepTwoViewSceneCompositorReplacementParity()) break;
                    Record(currentCase, "compositor", Game.CompositorParityVerificationEvidence!);
                    Run("compositor.slot-connections", "compositor", Game.VerifyCompositorSlotParity);
                }
                if (selectedFamilies.Contains("texture"))
                {
                    var textureResults = Game.VerifyTextureParityBatch();
                    Require(textureResults.Length == 6, "All three native texture policies require Linear and Gamma readback cases.");
                    foreach (var result in textureResults)
                        Record("texture." + result.PackageColorSpace + "." + result.Hint, "texture", result);
                }
                if (selectedFamilies.Contains("audio")) { SeekPhase(15); break; }
                SeekPhase(16);
                break;
            case 13:
                if (!selectedFamilies.Contains("save")) { SeekPhase(6); break; }
                RequireSharedOwners(); currentCase = "save.metadata-replacement";
                if (!Game.IsProjectSaveParityVerificationReady) break;
                saveRun = Game.BeginProjectSaveParityVerification(); Next(); break;
            case 14:
                RequireSharedOwners(); currentCase = "save.metadata-replacement";
                var saveEvidence = Game.StepProjectSaveParityVerification(saveRun!);
                if (saveEvidence == null) break;
                Record(currentCase, "save", saveEvidence); saveRun = null;
                Run("save.unknown-component-preservation", "save", () => Game.VerifyUnknownComponentParity(Path.Combine(Artifacts, "unknown-component")));
                mixedSaveRun = Game.BeginMixedSaveParityVerification(); SeekPhase(31); break;
            case 31:
                RequireSharedOwners(); currentCase = "save.mixed-conflict-retry";
                if (!Game.StepMixedSaveParityVerification(mixedSaveRun!)) break;
                Record(currentCase, "save", mixedSaveRun!.Evidence!); mixedSaveRun = null;
                recoveryIntegrityRun = Game.BeginRecoveryIntegrityParityVerification(Path.Combine(Artifacts, "recovery-integrity"));
                SeekPhase(37); break;
            case 37:
                RequireSharedOwners(); currentCase = "save.recovery-corrupt-journal-refusal";
                if (!Game.StepRecoveryIntegrityParityVerification(recoveryIntegrityRun!)) break;
                Record(currentCase, "save", recoveryIntegrityRun!.Evidence!); recoveryIntegrityRun = null;
                packageDiskConflictRun = Game.BeginPackageDiskConflictParityVerification(); SeekPhase(44); break;
            case 44:
                RequireSharedOwners(); currentCase = "save.package-disk-conflict-retry";
                if (!Game.StepPackageDiskConflictParityVerification(packageDiskConflictRun!)) break;
                Record(currentCase, "save", packageDiskConflictRun!.Evidence!); packageDiskConflictRun = null;
                SeekPhase(6); break;
            case 15:
                RequireSharedOwners(); currentCase = "audio.recovery";
                try
                {
                    audioRuntimeUrl ??= Game.ResolveAudioParityRuntimeUrl(audioFixture!);
                    if (!Game.StepAudioParityVerification(audioRuntimeUrl)) break;
                    Record(currentCase, "audio", Game.AudioParityEvidence!);
                }
                catch (Exception error)
                {
                    var receipt = new CaseReceipt(currentCase, "audio", false, new { error = error.ToString(), audioFixture, audioRuntimeUrl });
                    receipts.Add(receipt); WriteCase(receipt, false, false); throw;
                }
                SeekPhase(16); break;
            case 16:
                if (!selectedFamilies.Contains("visualscript")) { Next(); break; }
                RequireSharedOwners(); currentCase = "visualscript.generated-history";
                visualScriptHistory ??= new(Game, Path.Combine(Artifacts, "visualscript"))
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                visualScriptHistory.Update();
                if (!visualScriptHistory.Finished) break;
                Require(visualScriptHistory.Passed, visualScriptHistory.Result);
                Record(currentCase, "visualscript", visualScriptHistory.CapabilityEvidence);
                Game.StartVisualScriptTopologyVerification(Path.Combine(Artifacts, "visualscript-topology")); SeekPhase(29); break;
            case 29:
                RequireSharedOwners(); currentCase = "visualscript.graph-topology";
                var topologyEvidence = Game.StepVisualScriptTopologyVerification();
                if (topologyEvidence == null) break;
                Record(currentCase, "visualscript", topologyEvidence);
                Run("visualscript.legacy-inline-references", "visualscript", () => Game.VerifyVisualScriptLegacyReferences(Path.Combine(Artifacts, "visualscript-legacy")));
                SeekPhase(17); break;
            case 17:
                if (!selectedFamilies.Contains("stock-dispatch")) { SeekPhase(21); break; }
                RequireSharedOwners(); currentCase = "stock-dispatch.script";
                stockDispatch ??= new(Game, Path.Combine(Artifacts, "stock-dispatch"))
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                stockDispatch.Update();
                if (!stockDispatch.Finished) break;
                Require(stockDispatch.Passed, stockDispatch.Result);
                Record(currentCase, "stock-dispatch", stockDispatch.CapabilityEvidence);
                Game.StartScriptSemanticAssistanceVerification(Path.Combine(Artifacts, "semantic-assistance"));
                SeekPhase(38); break;
            case 38:
                RequireSharedOwners(); currentCase = "stock-dispatch.semantic-assistance";
                var semanticEvidence = Game.StepScriptSemanticAssistanceVerification();
                if (semanticEvidence == null) break;
                Record(currentCase, "stock-dispatch", semanticEvidence); SeekPhase(41); break;
            case 41:
                RequireSharedOwners(); currentCase = "stock-dispatch.completion-ui";
                scriptCompletionUi ??= new(Game, Path.Combine(Artifacts, "completion-ui"))
                    { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                scriptCompletionUi.Update();
                if (!scriptCompletionUi.Finished) break;
                Require(scriptCompletionUi.Passed, scriptCompletionUi.Result);
                Record(currentCase, "stock-dispatch", scriptCompletionUi.CapabilityEvidence); SeekPhase(18); break;
            case 18:
                RequireSharedOwners(); currentCase = "stock-dispatch.prefab";
                stockPrefabDispatch ??= new(Game, Path.Combine(Artifacts, "stock-dispatch", "prefab"), false)
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                stockPrefabDispatch.Update();
                if (!stockPrefabDispatch.Finished) break;
                Require(stockPrefabDispatch.Passed, stockPrefabDispatch.Result);
                Record(currentCase, "stock-dispatch", stockPrefabDispatch.CapabilityEvidence); Next(); break;
            case 19:
                RequireSharedOwners(); currentCase = "stock-dispatch.uilibrary";
                stockLibraryDispatch ??= new(Game, Path.Combine(Artifacts, "stock-dispatch", "uilibrary"), true)
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                stockLibraryDispatch.Update();
                if (!stockLibraryDispatch.Finished) break;
                Require(stockLibraryDispatch.Passed, stockLibraryDispatch.Result);
                Record(currentCase, "stock-dispatch", stockLibraryDispatch.CapabilityEvidence); Next(); break;
            case 20:
                RequireSharedOwners(); currentCase = "stock-dispatch.visualscript";
                if (!stockVisualScriptPrepared)
                {
                    if (!selectedFamilies.Contains("visualscript")) Game.CompleteVisualScriptHistoryFixturePreparation();
                    stockVisualScriptPrepared = true;
                }
                stockVisualScriptDispatch ??= new(Game, Path.Combine(Artifacts, "stock-dispatch", "visualscript"))
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                stockVisualScriptDispatch.Update();
                if (!stockVisualScriptDispatch.Finished) break;
                Require(stockVisualScriptDispatch.Passed, stockVisualScriptDispatch.Result);
                Record(currentCase, "stock-dispatch", stockVisualScriptDispatch.CapabilityEvidence);
                Next(); break;
            case 21:
                if (!selectedFamilies.Contains("asset-templates")) { SeekPhase(25); break; }
                RequireSharedOwners(); currentCase = "asset-templates.registered";
                if (templateLocations.Length == 0) templateLocations = Game.InspectAssetTemplateTargetNavigation();
                if (Click("tooltab/assets.folders")) Next(); break;
            case 22:
                if (templateLocationIndex == templateLocations.Length)
                { if (Click("tooltab/assets.browser")) SeekPhase(24); break; }
                if (Click("location/" + templateLocations[templateLocationIndex])) Next(); break;
            case 23:
                if (!Game.IsAssetTemplateTargetLocationSelected(templateLocations[templateLocationIndex])) break;
                if (templateLocationIndex + 1 < templateLocations.Length
                    && !Game.Controls.Expanded.GetValueOrDefault("location/" + templateLocations[templateLocationIndex]))
                { Hexa.NET.ImGui.ImGui.GetIO().AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.RightArrow, true); SeekPhase(26); break; }
                templateLocationIndex++; SeekPhase(22); break;
            case 26:
                Hexa.NET.ImGui.ImGui.GetIO().AddKeyEvent(Hexa.NET.ImGui.ImGuiKey.RightArrow, false);
                SeekPhase(23); break;
            case 24:
                RequireSharedOwners(); currentCase = "asset-templates.registered";
                if (assetTemplates == null && !Game.StockDispatchFiltersEmpty)
                { Click("asset/filter/clear"); break; }
                assetTemplates ??= new(Game, Path.Combine(Artifacts, "asset-templates"))
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                assetTemplates.Update();
                if (!assetTemplates.Finished) break;
                Require(assetTemplates.Passed, assetTemplates.Result);
                Record(currentCase, "asset-templates", assetTemplates.CapabilityEvidence); SeekPhase(27); break;
            case 27:
                RequireSharedOwners(); currentCase = "asset-templates.fbx-import";
                fbxTemplates ??= new(Game, Path.Combine(Artifacts, "fbx-import"))
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                fbxTemplates.Update();
                if (!fbxTemplates.Finished) break;
                Require(fbxTemplates.Passed, fbxTemplates.Result);
                Record(currentCase, "asset-templates", fbxTemplates.CapabilityEvidence);
                Run("asset-templates.browser-compound-filters", "asset-templates", () =>
                {
                    Game.VerifyAssetBrowserFilterParity();
                    return Game.AssetBrowserFilterParityEvidence ?? throw new InvalidOperationException("Browser filter verification produced no evidence.");
                });
                SeekPhase(33); break;
            case 33:
                RequireSharedOwners(); currentCase = "asset-templates.reimport-changed-batch";
                if (!Game.AssetReimportParityReady) break;
                reimport ??= new(Game, Path.Combine(Artifacts, "reimport"))
                { SharedTerminalTeardownPath = SharedTerminalTeardownPath };
                reimport.Update();
                if (!reimport.Finished) break;
                Require(reimport.Passed, reimport.Result);
                Record(currentCase, "asset-templates", reimport.CapabilityEvidence); SeekPhase(39); break;
            case 39:
                RequireSharedOwners(); currentCase = "asset-templates.asset-inheritance";
                if (!inheritanceVerificationStarted)
                {
                    Game.VerifyAssetInheritanceParity(); inheritanceVerificationStarted = true;
                }
                if (!Game.StepAssetInheritanceParityFinalSave()) break;
                Record(currentCase, "asset-templates", Game.AssetInheritanceParityEvidence!);
                Run("asset-templates.cross-volume-file-transfer", "asset-templates", () => AssetCrossVolumeParityVerification.Verify(Artifacts));
                crossVolumeRelocationRun = Game.BeginAssetCrossVolumeRelocationParityVerification(Artifacts); SeekPhase(46); break;
            case 46:
                RequireSharedOwners(); currentCase = "asset-templates.cross-volume-authored-relocation";
                if (!Game.StepAssetCrossVolumeRelocationParityVerification(crossVolumeRelocationRun!)) break;
                Record(currentCase, "asset-templates", crossVolumeRelocationRun!.Evidence!); crossVolumeRelocationRun = null;
                SeekPhase(25); break;
            case 25:
                if (!selectedFamilies.Contains("extensions")) { CompleteBatch(); break; }
                RequireSharedOwners(); currentCase = "extensions.provider-lifecycle";
                if (!extensionLifecycleStarted)
                {
                    Game.StartNativeExtensionOwnedEditorLifecycle(Game.InspectExtensionParityAsset());
                    extensionLifecycleStarted = true;
                }
                var extensionEvidence = Game.StepNativeExtensionOwnedEditorLifecycle();
                if (extensionEvidence == null) break;
                Record(currentCase, "extensions", extensionEvidence);
                CompleteBatch(); break;
        }
    }
    private void CompleteBatch()
    {
        Require(plannedCases.Order().SequenceEqual(receipts.Select(item => item.Id).Order()),
            "All planned grouped parity cases must publish their own successful evidence.");
        Finish(true, "F.group PASS: scoped parity cases used one actual loaded project; individual receipts describe input coverage and require outer terminal retirement.");
    }
    private void RequireSharedOwners() => Require(ReferenceEquals(workspaceOwner, Game.VerificationWorkspaceOwner)
        && ReferenceEquals(historyOwner, Game.VerificationHistoryOwner)
        && ReferenceEquals(compiledOwner, Game.VerificationCompiledProjectOwner),
        "Grouped parity cases must retain the actual loaded workspace/history/compiled-content owners.");
    private void Run(string id, string family, Func<object> operation)
    {
        currentCase = id;
        try { Record(id, family, operation()); }
        catch (Exception error)
        {
            var receipt = new CaseReceipt(id, family, false, new { error = error.ToString() });
            receipts.Add(receipt); WriteCase(receipt, false, false); throw;
        }
    }
    private void Record(string id, string family, object evidence)
    {
        Require(receipts.All(item => item.Id != id), "A grouped parity case must not execute twice: " + id);
        var receipt = new CaseReceipt(id, family, true, evidence); receipts.Add(receipt); WriteCase(receipt, false, false);
    }
    private void WritePlan()
    {
        plannedCases = new[] { "shell.contract", "shell.theme-preferences", "project.dependencies", "project.recent-projects", "project.active-project-switch", "project.pending-switch", "project.compiler-start-retirement" }.Concat(ModelParityCases.All.Select(item => item.Id))
            .Concat(new[] { "save.metadata-replacement", "save.unknown-component-preservation", "save.mixed-conflict-retry", "save.recovery-corrupt-journal-refusal" })
            .Concat(new[] { "inspector.contract", "inspector.progress", "inspector.script-authority", "inspector.dictionary-sealed", "inspector.aggregate-collection-atomicity", "inspector.nested-collection-stale-target", "inspector.property-clipboard-ownership", "inspector.automatic-preference-safety" }).Concat(SpriteParityCases.Cases)
            .Concat(new[] { "authority.context", "authority.resize", "authority.ui-canvas-ownership", "authority.scene-ui-split-ownership" })
            .Concat(new[] { "Linear", "Gamma" }.SelectMany(space => new[] { "Color", "Grayscale", "NormalMap" }
                .Select(hint => "texture." + space + "." + hint))).Concat(new[] { "audio.recovery", "compositor.refresh-certificate", "compositor.slot-connections", "inputs.polling-certificate", "thumbnails.idle-restart", "dependencies.restore", "library-dependencies.restore", "visualscript.generated-history", "visualscript.graph-topology", "visualscript.legacy-inline-references", "stock-dispatch.script", "stock-dispatch.semantic-assistance", "stock-dispatch.prefab", "stock-dispatch.uilibrary", "stock-dispatch.visualscript", "asset-templates.registered", "asset-templates.fbx-import", "asset-templates.browser-compound-filters", "asset-templates.reimport-changed-batch", "asset-templates.asset-inheritance", "extensions.provider-lifecycle" })
            .Concat(new[] { "asset-templates.cross-volume-file-transfer", "asset-templates.cross-volume-authored-relocation", "stock-dispatch.completion-ui", "project.stale-target-path", "inspector.polymorphic-object-lifecycle", "save.package-disk-conflict-retry", "inspector.polymorphic-external-alias-preservation", "project.folder-file-read-failure" })
            .Where(id => selectedFamilies.Contains(Family(id))).ToArray();
        File.WriteAllText(Path.Combine(Artifacts, "F.group-case-plan.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, scenario = Scenario, selectedFamilies, cases = plannedCases.Select(id => new
            { id, family = Family(id), coverage = CaseCoverage(id), resultFile = "cases/" + FileIdentity(id) + "-result.json" }) },
            new JsonSerializerOptions { WriteIndented = true }));
    }
    private void WriteCase(CaseReceipt receipt, bool teardownCompleted, bool outerPassed)
    {
        var directory = Path.Combine(Artifacts, "cases"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileIdentity(receipt.Id) + "-result.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scenario = receipt.Id, caseId = receipt.Id, family = receipt.Family,
            scenarioPassed = receipt.Passed, passed = teardownCompleted && outerPassed && receipt.Passed,
            teardownCompleted, sharedTerminalTeardownPath = SharedTerminalTeardownPath, coverage = CaseCoverage(receipt.Id), evidence = receipt.Evidence
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string FileIdentity(string id) => id.Replace(':', '-');
    private static string CaseCoverage(string id) => id == "inspector.automatic-preference-safety"
        ? "Separate native package/profile/canonical history: durable automatic persistence, real redo preservation, dirty/same-key/compound deferral, external-file conflict compensation and unpublished-owner rejection. No loaded-project mutation or settings widget claim. Outer terminal required."
        : id == "project.stale-target-path"
        ? "A genuine successful compiler request whose actual native target path changes before completion is rejected and retired without adoption. Protected canonical owners, history, world, camera and files remain exact. No fabricated task or path normalization. Outer terminal required."
        : id == "project.folder-file-read-failure"
        ? "Normal editor draw encounters actual missing and exclusively locked private project files, retains package roots and protected state, emits deduplicated native IO diagnostics and retries to a real recovered settings owner. No direct folder refresh, fake error or forced cache retirement. Outer terminal required."
        : id == "inspector.polymorphic-object-lifecycle"
        ? "Native aggregate nested-object factory create/replace/clear, independent identifiers, Undo/Redo, stale second-target atomic rejection and independent native persistence. Fixture arrangement uses private serialized types; no property-widget or external-reference cleanup claim. Outer terminal required."
        : id == "inspector.polymorphic-external-alias-preservation"
        ? "Actual owned polymorphic objects contain native Entity/Transform references. Replace/clear and Undo/Redo preserve canonical targets, other-owner links, owned identities and independently reloaded YAML aliases. No arbitrary custom-object incoming-reference cleanup, widget or cross-document claim. Outer terminal required."
        : id == "save.package-disk-conflict-retry"
        ? "A real canonical package save refuses an external file conflict after actual detached preparation, preserving dirty owner/history and other files; controlled baseline restoration allows normal retry and independent native reload. Metadata mutation is fixture arrangement. Solution reload only when an actual solution file exists; no whole-save atomicity claim. Outer terminal required."
        : id == "asset-templates.cross-volume-file-transfer"
        ? "Actual private source and destination on independently certified different native volumes; owned asset-file transfer, independent native YAML, collisions/stale hash rejection, reverse replay and observed compensation boundaries. No full package/reference/shared-history or relocation UI claim. Outer terminal required."
        : id == "asset-templates.cross-volume-authored-relocation"
        ? "Two real writable SDK packages on certified distinct volumes; actual authored material relocation, incoming model and package-root references, shared native history, collision/stale-disk compensation, normal save and independent project reload. Fixture arrangement and native command flow, not relocation UI or all asset types. Outer terminal required."
        : id == "project.pending-switch"
        ? "Real pending property widget validation, native authored commit before production target certification, genuine compiler cancellation/output retirement and actual switch/return. Fixture source and expected edit boundaries are explicit; no fabricated compiler tasks. Outer terminal required."
        : id == "project.compiler-start-retirement"
        ? "Real native SDK children, original progress-callback exception identity, actual exclusive log-open failure, genuine process/reader retirement and locked output preservation followed by physical archive/deletion. No fabricated task, project reopening or diagnostic output deletion. Outer terminal required."
        : id == "stock-dispatch.completion-ui"
        ? "Synthetic native Ctrl+Space and actual Roslyn completion popup Selectable activation, canonical commit, native toolbar replay/Save and independent bytes. Text/caret arrangement is not a navigation claim; no physical keyboard or code-action coverage. Outer terminal required."
        : id == "inspector.dictionary-sealed"
        ? "Production dictionary provider-or-fallback commands, typed keys, effective sealed-inheritance eligibility and mutation rejection, canonical history and independent native saved roundtrip; no widget click or physical input claim. Outer terminal required."
        : id == "project.active-project-switch"
        ? "Genuine two-project native solution, real second/first compiler requests and exact adopted outputs, preserved canonical owners/history/views/camera/source bytes, already-current target without a producer. Native success boundary; stale-target, pending-draft and combo gestures remain separate. Bundle lifetimes must retire normally with the host."
        : id == "inspector.nested-collection-stale-target"
        ? "Genuine nested owned collections, native parent/item identities, prepared mutation refusal after parent removal, fresh contexts after Undo, independent native persistence and baseline restoration. Service/native scope, not widget or physical input. Outer terminal required."
        : id == "authority.scene-ui-split-ownership"
        ? "Actual native document split and dock-back menus, simultaneously visible scene/UI engine authorities, UI pick and zoom/GPU changes isolated from scene camera, unchanged canonical history/data and restored layout. Synthetic input with native acceptance; no exact pixels or physical input. Outer terminal required."
        : id == "save.recovery-corrupt-journal-refusal"
        ? "Real native scene capture and asynchronous durable journal, controlled owned payload corruption, production integrity rejection, unchanged canonical/history/disk and independent native reload. Journal prerequisite only; genuine crash receiver refusal is a separate two-process case. Outer terminal required."
        : id == "stock-dispatch.semantic-assistance"
        ? "Shared real MSBuild/Roslyn workspace over an unsaved canonical source: diagnostics, completion commit, signature/definition, stale-result refusal, owned history and native saved-byte restoration. Service/native semantics, not assist popup or physical input. Pending jobs must retire before outer shutdown."
        : id == "asset-templates.asset-inheritance"
        ? "Actual native derived/replacing asset creation and link clearing, exact canonical/history identities, independent native YAML and file replay, baseline restoration plus asynchronous package Save All. Native service scope for the fixture type; no UI chooser, preview or every inherited asset type claim. Outer terminal required."
        : id == "authority.ui-canvas-ownership"
        ? "Actual compiled native UI canvas pick, held move/resize and Escape cancellation, accepted toolbar history/Save, independent native layout persistence and baseline restoration. Synthetic GUI with recorded native canvas/engine geometry; no desktop input or exact-pixel claim. Outer terminal required."
        : id == "asset-templates.reimport-changed-batch"
        ? "Native changed-source reimport publication, only-modified peer exclusion and cancellation before publication with actual task drain, native identity/source certificates/history/persistence and baseline restoration. Service/native scope; no typed FBX merge, child-process cancellation, UI gesture or preview claim. Outer terminal required."
        : id == "shell.theme-preferences"
        ? "Production settings draft discard/Save, independent durable theme values, actual native palette and noncompounding normal-frame DPI metrics with private state restoration. Service/frame-boundary scope; no Cancel button or mixed-monitor claim. Outer terminal required."
        : id == "save.mixed-conflict-retry"
        ? "Actual mixed canonical Save All disk conflict, earlier per-file acknowledgments, retained failing dirty owner and normal retry, independent native reload and owned history/baseline cleanup. Service/native scope; no whole-save atomicity or toolbar gesture claim. Outer terminal required."
        : id == "visualscript.legacy-inline-references"
        ? "Real legacy inline endpoint YAML, native in-memory alias normalization, compiler output, unchanged source until explicit Save and independent native reload; malformed endpoint rejection before mutation. No automatic on-disk upgrade or canvas gesture claim. Outer terminal required."
        : id == "inspector.property-clipboard-ownership"
        ? "Actual in-memory native property clipboard payloads, typed values and independent owned clones, canonical references and atomic unresolved foreign-reference rejection, history and stale/sealed rejection, native persistence and baseline restoration. Service/native scope; no OS clipboard, swapped generation or widget claim. Outer terminal required."
        : id == "save.unknown-component-preservation"
        ? "Real native unknown-component YAML recovery, canonical known-sibling authoring and history, native save and independent unknown-event/ID preservation. Isolated native fixture; no unknown-component compilation, GPU rendering or UI-open claim. Outer terminal required."
        : id == "asset-templates.browser-compound-filters"
        ? "Production browser control model over actual workspace assets: compound type/tag filters, ordering and selection identities; canonical native tags, history and independent persistence with baseline restoration. No UI gestures or platform-input claim. Outer terminal required."
        : id == "compositor.slot-connections"
        ? "Actual authored compositor slot certificates and guarded connection actions, incompatible and stale endpoint rejection, native history and independent serialized alias identity with fixture restoration. Service/native scope; no slot UI gestures or renderer-output claim. Outer terminal required."
        : id == "project.recent-projects"
        ? "Actual isolated native MRU version buckets, limits, eligible presentation, remove and stale-remove rejection, independent native reload and exact private settings restoration. Service/native scope; no project-open or platform input claim. Outer terminal required."
        : id == "inspector.aggregate-collection-atomicity"
        ? "Real two-component collection edits, stable native item identities, one canonical transaction, no-op history, stale-target rollback and sealed-owner rejection, independent native persistence and baseline restoration. Service/native scope; no widget or cross-document claim. Outer terminal required."
        : id == "visualscript.graph-topology"
        ? "Native registered block factories, actual slots and links, canonical history, real generated compiler projection and independent native persistence with baseline restoration. Service/native authoring; no canvas gestures or physical input claim. Outer terminal required."
        : id == "asset-templates.fbx-import"
        ? "Registered Model/Skeleton/Animation imports with native options, batch publication, references, source hashes, history and saved roundtrip. Retained file-source selection is a diagnostic seam, not platform dialog input. Outer terminal required."
        : Family(id) == "stock-dispatch"
        ? "Synthetic native stock browser tile selection and dedicated-editor button dispatch, authored widget input/history/Save and clean tab close/reopen; independent native asset/source bytes and ownership proof. Fixture construction is arrangement; native double-click timing and physical input are not covered."
        : Family(id) == "asset-templates"
        ? "Synthetic native chooser/name/Create/Cancel and texture-reference picker; four branch receipts: procedural, Heightmap-source, SpriteSheet-source and Skybox-reference. File-source selection uses a retained diagnostic seam, not platform dialog input. Native publication/collision/history/serialization and source hashes; outer terminal required."
        : Family(id) == "extensions"
        ? "Native provider/editor registration and retained activation; virtual updater/command eligibility/edit/no-op/history and adapter template publication use services. Actual in-frame native provider window rendering is certified separately; no UI command clicks, CLR unload or WPF binary compatibility claim. Outer terminal required."
        : Family(id) == "visualscript"
        ? "Synthetic native UI add-property and toolbar Undo/Redo; native compiler, ownership, history and file proof. Fixture arrangement uses services; physical input is not covered."
        : Coverage;
    private static string Family(string id) => id.StartsWith("M", StringComparison.Ordinal) ? "model"
        : id.StartsWith("SP", StringComparison.Ordinal) ? "sprite" : id.Split('.')[0];
    protected override void CleanInput()
    {
        Game.CleanSceneUiGroupParityInput();
        Game.CancelScriptSemanticAssistanceVerification();
        Game.CancelScriptCompletionUiVerification();
        if (themeRun != null) { Game.EndThemePreferencesParityVerification(themeRun); themeRun = null; }
        Game.CancelDictionaryContractVerification();
        Game.CancelVisualScriptTopologyVerification();
        Game.CancelNativeExtensionOwnedEditorLifecycle();
        thumbnailIdleCancellation.Cancel();
        if (thumbnailIdleRun != null) Game.QueueShellParityAsyncCleanup(thumbnailIdleRun, thumbnailIdleCancellation);
        else thumbnailIdleCancellation.Dispose();
        if (dependencyRestoreRun != null)
        {
            Game.CancelP02DependencyRestoreParity();
            Game.QueueShellParityAsyncCleanup(dependencyRestoreRun);
        }
        if (libraryDependencyRun != null)
        {
            Game.CancelP02NugetLibraryParity();
            Game.QueueShellParityAsyncCleanup(libraryDependencyRun);
        }
        try
        {
            if (saveRun != null) Game.QueueShellParitySaveCleanup(saveRun);
            try { if (scriptFixture != null) Game.CloseScriptPresentationAuthorityVerification(scriptFixture); }
            finally
            {
                try { Game.CloseSpriteParityFixture(); }
                finally
                {
                    try { Game.CancelAuthorityParityVerification(); }
                    finally { Game.CancelAuthorityResizeVerification(); }
                }
            }
        }
        finally { Game.CancelAudioParityVerification(); }
    }
    internal override void FinalizeCompletion(bool passed, string result)
    {
        if (assetTemplates != null)
        {
            assetTemplates.SharedTerminalTeardownPath = SharedTerminalTeardownPath;
            assetTemplates.FinalizeCompletion(passed && assetTemplates.Passed, assetTemplates.Result);
        }
        if (stockDispatch != null)
        {
            stockDispatch.SharedTerminalTeardownPath = SharedTerminalTeardownPath;
            stockDispatch.FinalizeCompletion(passed && stockDispatch.Passed, stockDispatch.Result);
        }
        foreach (var child in new EngineUiCapabilityProbe?[] { stockPrefabDispatch, stockLibraryDispatch, stockVisualScriptDispatch, fbxTemplates, uiCanvas, reimport, activeProjectPending, scriptCompletionUi })
            if (child != null)
            {
                child.SharedTerminalTeardownPath = SharedTerminalTeardownPath;
                child.FinalizeCompletion(passed && child.Passed, child.Result);
            }
        if (visualScriptHistory != null)
        {
            visualScriptHistory.SharedTerminalTeardownPath = SharedTerminalTeardownPath;
            visualScriptHistory.FinalizeCompletion(passed && visualScriptHistory.Passed, visualScriptHistory.Result);
        }
        foreach (var receipt in receipts) WriteCase(receipt, true, passed);
        base.FinalizeCompletion(passed, result);
    }
}
