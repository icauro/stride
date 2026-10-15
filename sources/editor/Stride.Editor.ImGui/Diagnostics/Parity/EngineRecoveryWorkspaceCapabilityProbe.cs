using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed record RecoveryWorkspaceDiskReceipt(string Path, string Sha256);
internal sealed record RecoveryWorkspaceDockReceipt(string Path, Guid GroupId, uint DockId,
    uint ParentDockId, int Axis);
internal sealed record RecoveryWorkspaceSourceReceipt(RecoverySourceVerificationReceipt Membership, string EditedText, string PayloadHash);
internal sealed record RecoveryWorkspaceAcceptanceReceipt(int Version, int ProcessId, long ProcessStartedUtcTicks,
    string ProjectPath, string ManifestPath, string ManifestSha256, Guid SessionId,
    EditorRecoveryWorkspaceState Workspace, string DirtyPath, Guid DirtyEntity, string DirtyPayloadHash, float PositionX,
    RecoveryWorkspaceDiskReceipt[] OriginalDisks, RecoveryWorkspaceDockReceipt[] NativeDocks,
    RecoveryWorkspaceSourceReceipt[] Sources, RecoveryGeneratedReadOnlySourcesReceipt? GeneratedReadOnlySources,
    RecoveryMissingGeneratedProducerReceipt? MissingGeneratedProducer);

/// <summary>Actual crash producer and real queued workspace recovery UI receiver over one owned private fixture.</summary>
internal sealed partial class EngineRecoveryWorkspaceCapabilityProbe : EngineUiCapabilityProbe
{
    private readonly bool producer;
    private readonly string receiptPath, journalRoot;
    private MultiViewFixtureVerification? fixture;
    private EntityHierarchyDocument? first, dirty;
    private Guid firstId, secondId, dirtyEntity;
    private Task? checkpoint;
    private RecoveryWorkspaceAcceptanceReceipt? receipt;
    private RecoveryWorkspaceDiskReceipt[] disks = [];
    private bool durableReady, restored;
    private int stable;
    private Guid? preDataRestoreGeneration;
    private RecoverySourceVerificationReceipt[] sourceMembership = [];
    private string[] sourceTexts = [];
    private int sourceIndex;
    private string sourceProject = "", includedSource = "", linkedSource = "", excludedSource = "";
    private object? sourceRejections;
    private string? externalSourceHash, saveFailure;
    private long? sourceVersionBeforeSave;
    private ScriptSourceDocument? sourceOwnerBeforeSave;
    private Guid? sourceGenerationBeforeSave;
    private string? sourceHistoryBeforeSave;
    private object? restoredFocus;
    private readonly Dictionary<string, Guid?> cleanSourceGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> restoredSceneRenderBaselines = new(StringComparer.OrdinalIgnoreCase);
    private object? restoredPaneEvidence;
    private int recoveryWheelEvents;
    private double nextRecoveryWheel;
    private string? recoveryScrollTarget;
    private float recoveryScrollDirection;
    private object? nativeIniComparison;
    private readonly bool compensation;
    private bool compensationArmed;
    private EditorRecoveryWorkspaceState? compensationBaseline;
    private string? compensationHistory;
    private RecoveryWorkspaceDiskReceipt[] compensationDisks = [];
    private object? recoveryCompensation, compensationOwnerProof;
    private int compensationBaselineStableFrames;
    private string? compensationBaselineStableKey;
    private long? compensationBaselineFirstRender;
    private object? compensationBaselineReadiness;
    private object? expectedOutputActivation;
    private object? compensationBeforeModalClose;
    private readonly bool dirtyRetainedOwner;
    private bool dirtyOwnerPrepared;
    private EntityHierarchyDocument? retainedDirtyScene;
    private Guid retainedDirtyEntry, retainedDirtyGeneration, retainedDirtyEntity, ownedDirtyTransaction;
    private float retainedOriginalX;
    private EditorHistorySnapshot? retainedCleanHistory;
    private object? dirtyOwnerCompensationProof;
    private readonly Queue<object> restoredNativeFrameEvidence = new();
    private bool compensationDocumentFocusVerified;
    private int compensationDocumentFocusFrame = -1;
    private object? compensationOutputFocus;
    private int compensationRestoreClickFrame = -1;
    private readonly bool generatedReadOnlySources;
    private RecoveryGeneratedReadOnlySourcesReceipt? generatedReadOnlyReceipt;
    private int protectedSourceIndex;
    private long protectedSourceVersion;
    private Guid? protectedSourceGeneration;
    private string? protectedSourceText, protectedSourceHistory;
    private readonly List<object> protectedTypingProofs = [];
    private object? protectedSourceOwnership;
    private object? protectedNavigationReadiness;
    private string? protectedAttemptDeadlinePath;
    private double protectedAttemptDeadline;
    private int protectedTabClickAttempts;
    private int protectedScrollClickAttempts;
    private bool releaseProtectedScrollClick;
    private object? protectedScrollProof;
    private const float EditedX = 7.125f;
    protected override string Scenario => producer ? "E08.recovery.write" : "E08.recovery.read";
    protected override double PhaseTimeoutSeconds => durableReady ? 3600 : !producer && Phase is 70 or 80 or 120 or 121 ? 30 : Phase <= 2 || !producer ? 180 : 90;
    protected override object Evidence => new
    {
        coverage = "Two real editor processes, actual synthetic property/tab/recovery controls and durable native journal; external runner certifies owned crash. No physical input or OS focus.",
        producer, durableReady, restored, receiptPath, journalRoot, firstId, secondId, dirtyEntity,
        restorePending = Game.IsRecoveryWorkspaceRestorePending, restoreFailure = Game.RecoveryWorkspaceRestoreFailure,
        expected = receipt, sceneDirty = dirty?.IsDirty,
        actualTransformLayout = Game.InspectRecoveryTransformLayout(),
        sourceMembership, sourceRejections, sourceIndex, externalSourceHash, saveFailure, restoredFocus,
        restoredPaneEvidence,
        recoveryWheelEvents, recoveryScrollTarget, recoveryScrollDirection, recoveryModal = DescribeRecoveryModal(),
        nativeIniComparison,
        CorruptPayloadRecovery,
        AmbiguousSourceRecovery,
        compensation, recoveryCompensation, compensationOwnerProof,
        compensationBaselineStableFrames, compensationBaselineFirstRender, compensationBaselineReadiness,
        expectedOutputActivation,
        compensationBeforeModalClose,
        dirtyRetainedOwner, dirtyOwnerPrepared, retainedDirtyEntry, retainedDirtyGeneration, retainedDirtyEntity,
        ownedDirtyTransaction, retainedOriginalX, dirtyOwnerCompensationProof,
        restoredNativeFrameEvidence = restoredNativeFrameEvidence.ToArray(),
        compensationDocumentFocusVerified, compensationDocumentFocusFrame, compensationOutputFocus,
        compensationRestoreClickFrame,
        generatedReadOnlySources, generatedReadOnlyReceipt, protectedSourceIndex, protectedTypingProofs, protectedSourceOwnership,
        protectedNavigationReadiness,
        protectedAttemptDeadlinePath, protectedAttemptDeadline, protectedTabClickAttempts,
        protectedScrollClickAttempts, protectedScrollProof,
        missingGeneratedDirtyProducer, missingGeneratedProducerReceipt, missingPreRestoreBoundary,
        MissingGeneratedRecovery,
        unsavedGeneratedProducer, unsavedReceipt, unsavedProducerState, unsavedConsentedState, unsavedRestoredState,
        unsavedDisabledControl, unsavedDependencyBlocked, UnsavedGeneratedRecovery,
        protectedNativeInputs = generatedReadOnlyReceipt != null && Game.Document != null
            ? new[] { generatedReadOnlyReceipt.GeneratedPath, generatedReadOnlyReceipt.ReadOnlyPath, generatedReadOnlyReceipt.ExternalReadOnlyPath }
                .Select(path => Game.InspectRecoverySourceReadiness(path)).ToArray() : null,
        compensationInspection = compensation ? Game.InspectRecoveryWorkspaceCompensationVerification() : null,
        nativeDockSelectionRestore = Game.InspectNativeDockSelectionRestore(),
        sourceInputs = sourceMembership.Select(item => Game.InspectRecoverySourceReadiness(item.Descriptor.Path)).ToArray(),
    };
    internal EngineRecoveryWorkspaceCapabilityProbe(EditorGame game, string artifacts, bool producer) : base(game, artifacts)
    {
        this.producer = producer;
        var arguments = Environment.GetCommandLineArgs();
        compensation = !producer && arguments.Contains("--recovery-compensation-after-scene-open");
        dirtyRetainedOwner = !producer && arguments.Contains("--recovery-compensation-dirty-owner");
        generatedReadOnlySources = arguments.Contains("--recovery-generated-readonly-sources");
        missingGeneratedDirtyProducer = arguments.Contains("--recovery-missing-generated-dirty-producer");
        unsavedGeneratedProducer = arguments.Contains("--recovery-unsaved-generated-producer");
        if (unsavedGeneratedProducer && (generatedReadOnlySources || missingGeneratedDirtyProducer || compensation))
            throw new ArgumentException("Unsaved native producer dependency recovery is a separate fixture variant.");
        if (missingGeneratedDirtyProducer && !generatedReadOnlySources) throw new ArgumentException("Missing generated recovery requires its actual protected-source fixture.");
        if (generatedReadOnlySources && compensation) throw new ArgumentException("Protected-source recovery and controlled compensation are distinct variants.");
        if (dirtyRetainedOwner && !compensation) throw new ArgumentException("Dirty owner compensation requires the explicit controlled failure receiver.");
        string Argument(string name)
        {
            var index = Array.IndexOf(arguments, name);
            if (index < 0 || index + 1 >= arguments.Length) throw new ArgumentException(name + " is required");
            return Path.GetFullPath(arguments[index + 1]);
        }
        receiptPath = Argument("--recovery-acceptance-receipt");
        journalRoot = Argument("--recovery-test-root");
        if (!producer && unsavedGeneratedProducer)
        {
            unsavedReceipt = JsonSerializer.Deserialize<RecoveryUnsavedGeneratedAcceptanceReceipt>(File.ReadAllBytes(receiptPath))
                ?? throw new InvalidDataException("Actual unsaved native producer receipt is missing.");
            unsavedProducer = unsavedReceipt.Producer;
            disks = unsavedReceipt.OriginalDisks;
        }
        else if (!producer)
        {
            receipt = JsonSerializer.Deserialize<RecoveryWorkspaceAcceptanceReceipt>(File.ReadAllBytes(receiptPath))
                ?? throw new InvalidDataException("Recovery producer receipt is missing");
            generatedReadOnlyReceipt = receipt.GeneratedReadOnlySources;
            missingGeneratedProducerReceipt = receipt.MissingGeneratedProducer;
            if (missingGeneratedDirtyProducer && missingGeneratedProducerReceipt is not { ActualNativeProducerEdit: true })
                throw new InvalidDataException("Missing generated recovery requires the actual dirty native producer receipt.");
            if (generatedReadOnlySources && generatedReadOnlyReceipt == null) throw new InvalidDataException("Protected sources require their genuine native producer receipt.");
        }
    }
    protected override void Step()
    {
        if (StepAmbiguousSourceScenario()) return;
        if (StepCorruptPayloadScenario()) return;
        if (unsavedGeneratedProducer) { StepUnsavedGeneratedScenario(); return; }
        if (missingGeneratedDirtyProducer && StepMissingGeneratedScenario()) return;
        if (generatedReadOnlySources && (producer && Phase is >= 80 and <= 85 || !producer && Phase is >= 120 and <= 125))
        { StepProtectedSourceTyping(producer ? 80 : 120, producer ? (missingGeneratedDirtyProducer ? 140 : 60) : 43); return; }
        if (!producer && Phase is 70 or 80)
        {
            restoredNativeFrameEvidence.Enqueue(new { frame = Gui.GetFrameCount(), phase = Phase,
                helper = Game.InspectNativeDockSelectionRestore(), tools = Game.InspectRecoveryAcceptanceToolTabs() });
            while (restoredNativeFrameEvidence.Count > 8) restoredNativeFrameEvidence.Dequeue();
        }
        if (producer) Produce(); else Receive();
    }
    private void Produce()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                if (fixture == null)
                {
                    fixture = Game.PrepareMultiViewVerificationFixture();
                    if (generatedReadOnlySources) Game.PrepareRecoveryGeneratedVisualScriptFixture();
                }
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented()) break;
                first = Game.Document; firstId = Game.VerificationDocumentId(first)!.Value;
                Require(!first.IsDirty, "Recovery starts with a real clean first scene");
                if (Click("asset/filter")) Next(); break;
            case 3: SelectAll(true); Next(); break;
            case 4: SelectAll(false); Type("CapabilitySecondScene"); Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 7: if (Click("asset/CapabilitySecondScene")) Next(); break;
            case 8: if (Click("asset/CapabilitySecondScene")) Next(); break;
            case 9:
                dirty = Game.VerificationActiveHierarchy;
                if (dirty?.Asset.Id != fixture!.SceneId || Game.VerificationDocumentViewport(dirty)?.RuntimeScene == null) break;
                secondId = Game.VerificationDocumentId(dirty)!.Value;
                Require(!dirty.IsDirty && !ReferenceEquals(dirty, first), "Second recovery document must be a distinct clean canonical owner");
                Next(); break;
            case 10:
                if (!Game.DocumentGroupParityCaseQuiescent || !Game.Controls.Centers.TryGetValue("document-tab/" + secondId, out var point)
                    || !Aim("recovery-tab-context", point)) break;
                io.AddMouseButtonEvent(1, true); Next(); break;
            case 11: io.AddMouseButtonEvent(1, false); Next(); break;
            case 12: if (Click("document-command/" + secondId + "/Horizontal")) Next(); break;
            case 13:
                var a = Game.InspectDocumentGroupParity(firstId); var b = Game.InspectDocumentGroupParity(secondId);
                if (a == null || b == null || !Game.DocumentGroupParityCaseQuiescent || !a.Visible || !b.Visible
                    || a.DockId == 0 || b.DockId == 0 || a.DockId == b.DockId || a.GroupId == b.GroupId) break;
                Require(a.ParentDockId != 0 && a.ParentDockId == b.ParentDockId && b.ParentSplitAxis == (int)ImGuiAxis.Y,
                    "Producer must genuinely establish horizontal native split panes via tab context command");
                if (Click("entity/Secondary Imported cube")) Next(); break;
            case 14:
                if (dirty!.Selected?.Name != "Secondary Imported cube") break;
                dirtyEntity = dirty.Selected.Id;
                Require(dirty.Selected.Transform.Position.X != EditedX, "Recovery edit must differ from persisted data");
                disks = new[] { first!, dirty }.Select(owner => new RecoveryWorkspaceDiskReceipt(owner.Path, Hash(owner.Path))).ToArray();
                if (ClickInspector("Position/0")) Next(); break;
            case 15: SelectAll(true); Next(); break;
            case 16: SelectAll(false); Type(EditedX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); Next(); break;
            case 17: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 18: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 19: if (Click("entity/Secondary Imported cube")) Next(); break;
            case 20:
                AssertDirtyData(); AssertOriginalDisks();
                using (var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Game.CaptureRecoveryWorkspaceVerification().ProjectPath)!, ".workspace-recovery-fixture.json"))))
                {
                    sourceProject = marker.RootElement.GetProperty("sourceProjectPath").GetString()!;
                    includedSource = marker.RootElement.GetProperty("includedSourcePath").GetString()!;
                    linkedSource = marker.RootElement.GetProperty("linkedSourcePath").GetString()!;
                    excludedSource = marker.RootElement.GetProperty("excludedSourcePath").GetString()!;
                }
                sourceMembership = Game.PrepareRecoverySourceMembershipVerification(sourceProject, includedSource, linkedSource);
                if (generatedReadOnlySources) generatedReadOnlyReceipt = Game.PrepareRecoveryGeneratedReadOnlySources();
                disks = disks.Concat(new[] { sourceProject, excludedSource }.Select(path => new RecoveryWorkspaceDiskReceipt(path, Hash(path)))).ToArray();
                if (generatedReadOnlyReceipt != null)
                    disks = disks.Concat(new[] { generatedReadOnlyReceipt.ProducerPath, generatedReadOnlyReceipt.GeneratedPath, generatedReadOnlyReceipt.ReadOnlyPath,
                        generatedReadOnlyReceipt.ExternalReadOnlyPath,
                        Path.Combine(Path.GetDirectoryName(sourceProject)!, "Scripts", "RecoveryOrphan.sdvs.cs") }
                        .Select(path => new RecoveryWorkspaceDiskReceipt(path, Hash(path)))).ToArray();
                Require(sourceMembership.Length == 2, "Included and linked sources must have real native evaluated owners");
                sourceRejections = Game.VerifyRecoverySourceMembershipRejections(sourceMembership[0], excludedSource);
                sourceTexts = sourceMembership.Select(item => Game.FindRecoverySourceVerificationDocument(item.Descriptor.Path)!.Text + "\n// Recovery Unicode: Ω 日本語 🚀\n").ToArray();
                sourceIndex = 0; SeekPhase(30); break;
            case 21:
                if (!checkpoint!.IsCompleted) break;
                checkpoint.GetAwaiter().GetResult();
                AssertDirtyData(); AssertOriginalDisks();
                var manifests = Directory.EnumerateFiles(journalRoot, "session.json", SearchOption.AllDirectories)
                    .Select(path => (Path: path, Data: JsonSerializer.Deserialize<EditorRecoveryManifest>(File.ReadAllBytes(path))))
                    .Where(item => item.Data?.ProcessId == Environment.ProcessId && item.Data.Workspace != null && !item.Data.Completed)
                    .OrderByDescending(item => item.Data!.CapturedUtc).ToArray();
                Require(manifests.Length == 1, "Exactly one durable recovery session must belong to this producer");
                var manifest = manifests[0];
                var committedWorkspace = manifest.Data?.Workspace ?? throw new InvalidDataException("The committed recovery manifest has no workspace presentation.");
                Require(manifest.Data!.Documents.Any(entry => Same(entry.Path, dirty!.Path))
                    && manifest.Data.Workspace!.Documents.Length == (generatedReadOnlySources ? 8 : 4) && manifest.Data.Workspace.Groups.Groups.Count(group => group.Paths.Length != 0) == 2,
                    "Committed journal must include the dirty scene, included/linked native source payloads and both actual document groups");
                foreach (var entry in manifest.Data.Documents)
                    Require(Hash(Path.Combine(Path.GetDirectoryName(manifest.Path)!, entry.PayloadHash + ".snapshot")) == entry.PayloadHash,
                        "Every committed authored recovery payload must have its genuine digest");
                var dock = new[] { Game.InspectDocumentGroupParity(firstId)!, Game.InspectDocumentGroupParity(secondId)! }
                    .Select(view => new RecoveryWorkspaceDockReceipt(view.Entry.Path, view.GroupId, view.DockId, view.ParentDockId, view.ParentSplitAxis)).ToArray();
                if (missingGeneratedDirtyProducer)
                    missingGeneratedProducerReceipt = CaptureMissingGeneratedProducerReceipt(manifest.Data!, generatedReadOnlyReceipt!);
                receipt = new(1, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    manifest.Data.ProjectPath, manifest.Path, Hash(manifest.Path), manifest.Data.SessionId,
                    committedWorkspace, dirty!.Path, dirtyEntity,
                    manifest.Data.Documents.Single(entry => Same(entry.Path, dirty.Path)).PayloadHash, EditedX, disks, dock,
                    sourceMembership.Select((item, index) => new RecoveryWorkspaceSourceReceipt(item, sourceTexts[index],
                        manifest.Data.Documents.Single(entry => Same(entry.Path, item.Descriptor.Path)).PayloadHash)).ToArray(), generatedReadOnlyReceipt, missingGeneratedProducerReceipt);
                if (generatedReadOnlySources)
                {
                    Require(!manifest.Data.Documents.Any(entry => Same(entry.Path, generatedReadOnlyReceipt!.GeneratedPath)
                        || Same(entry.Path, generatedReadOnlyReceipt.ReadOnlyPath) || !missingGeneratedDirtyProducer && Same(entry.Path, generatedReadOnlyReceipt.ProducerPath)
                        || Same(entry.Path, generatedReadOnlyReceipt.ExternalReadOnlyPath)),
                        "Clean generated producer/output and read-only views must have no authored dirty recovery payloads");
                    protectedSourceOwnership = missingGeneratedDirtyProducer
                        ? Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, requireMissing: false)
                        : Game.VerifyRecoveryGeneratedReadOnlySources(generatedReadOnlyReceipt!);
                }
                Require(Same(receipt.Workspace.ActivePath!, dirty.Path), "Dirty second document must be actual active recovery owner");
                AtomicReceipt(); durableReady = true; Next(); break;
            case 22:
                // Deliberately alive. External owned-process crash is the test;
                // normal Finish/Close would mark this journal completed.
                AssertOriginalDisks(); break;
            case 30:
                var input = Game.InspectRecoverySourceInput(sourceMembership[sourceIndex].Descriptor.Path);
                if (input != null && Click("document-tab/" + input.Value.EntryId)) Next(); break;
            case 31:
                var surface = Game.InspectRecoverySourceInput(sourceMembership[sourceIndex].Descriptor.Path);
                if (surface?.Center is { } center && Aim("recovery-source/" + sourceIndex, center)) { io.AddMouseButtonEvent(0, true); Next(); } break;
            case 32: io.AddMouseButtonEvent(0, false); Next(); break;
            case 33:
                if (Game.InspectRecoverySourceInput(sourceMembership[sourceIndex].Descriptor.Path)?.InputActive != true) break;
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.End, true); Next(); break;
            case 34: io.AddKeyEvent(ImGuiKey.End, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Next(); break;
            case 35: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 36: io.AddKeyEvent(ImGuiKey.Enter, false); Type("// Recovery Unicode: Ω 日本語 🚀"); Next(); break;
            case 37: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 38: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 39:
                var edited = Game.FindRecoverySourceVerificationDocument(sourceMembership[sourceIndex].Descriptor.Path);
                if (edited?.Text != sourceTexts[sourceIndex]) break;
                Require(edited.IsDirty && edited.CanEditText && Hash(edited.Path) == sourceMembership[sourceIndex].DiskSha256,
                    "Actual Unicode source surface edit must dirty the canonical owner while preserving its native disk baseline");
                if (++sourceIndex < sourceMembership.Length) SeekPhase(30);
                else { protectedSourceIndex = 0; SeekPhase(generatedReadOnlySources ? 80 : 60); }
                break;
            case 60: if (Click("document-tab/" + secondId)) Next(); break;
            case 61:
                AssertDirtyData(); AssertOriginalDisks();
                checkpoint = Game.BeginRecoveryWorkspaceVerificationCheckpoint(); SeekPhase(21); break;
        }
    }
    private void Receive()
    {
        switch (Phase)
        {
            case 0:
                Require(receipt!.Version == 1 && Hash(receipt.ManifestPath) == receipt.ManifestSha256,
                    "Receiver requires the producer's actual intact durable manifest");
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || Game.VerificationDocumentViewport(Game.Document)?.RuntimeScene == null) break;
                Require(dirtyOwnerPrepared || !Game.Document.IsDirty, "Normal receiver project open must initially load original clean disk data");
                if (missingGeneratedDirtyProducer && !ValidateMissingGeneratedPreRestoreBoundary()) break;
                AssertOriginalDisks();
                using (var conflict = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(receiptPath)!, "external-source-conflict.json"))))
                {
                    includedSource = conflict.RootElement.GetProperty("Path").GetString()!;
                    externalSourceHash = conflict.RootElement.GetProperty("ExternalSha256").GetString()!;
                    Require(Hash(includedSource) == externalSourceHash && receipt!.Sources.Single(item => Same(item.Membership.Descriptor.Path, includedSource)).Membership.DiskSha256 != externalSourceHash,
                        "Receiver must encounter the real externally changed included-source baseline after the producer crash");
                }
                if (compensation)
                {
                    if (dirtyRetainedOwner && !dirtyOwnerPrepared) { SeekPhase(90); break; }
                    if (!PrepareCompensationBaselineReadiness()) break;
                }
                Next(); break;
            case 3:
                if (compensation && !PrepareCompensationBaselineReadiness()) break;
                if (ClickRecovery("recovery/workspace/" + receipt!.SessionId, 1))
                {
                    if (compensation && !compensationArmed)
                    {
                        // The real click is queued, not consumed until the next native frame.
                        // Capture and arm at this same settled pre-command boundary.
                        compensationBaseline = Game.CaptureRecoveryWorkspaceVerification();
                        compensationHistory = Game.CaptureRecoveryAcceptanceHistory();
                        compensationDisks = receipt!.OriginalDisks.Select(item => item.Path)
                            .Concat(receipt.Sources.Select(item => item.Membership.Descriptor.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(path => new RecoveryWorkspaceDiskReceipt(path, Hash(path))).ToArray();
                        Game.ArmRecoveryWorkspaceCompensationVerification(); compensationArmed = true;
                        compensationRestoreClickFrame = Gui.GetFrameCount();
                    }
                    Next();
                }
                break;
            case 4:
                if (Game.IsRecoveryWorkspaceRestorePending) break;
                if (compensation)
                {
                    compensationOwnerProof = Game.VerifyRecoveryWorkspaceCompensation();
                    var beforeClose = Game.CaptureRecoveryWorkspaceVerification();
                    compensationBeforeModalClose = new { actualIni = beforeClose.NativeIni, expectedIni = compensationBaseline?.NativeIni,
                        dockingEqualAtObservation = compensationBaseline != null && DockingIni(beforeClose.NativeIni) == DockingIni(compensationBaseline.NativeIni),
                        modalStillOpen = Game.InspectRecoveryAcceptanceModal() != null,
                        nativeSelectionRestore = Game.InspectNativeDockSelectionRestore(),
                        limitation = "Observation before modal dismissal; queued native tool selections may not yet be acknowledged." };
                    if (ClickRecovery("recovery/close", -1)) SeekPhase(80);
                    break;
                }
                Require(Game.RecoveryWorkspaceRestoreFailure == null, "Actual queued canonical workspace recovery must succeed");
                // The recovery decision modal deliberately remains open after workspace consent.
                // Native pane focus is meaningful only after its real Close control dismisses it.
                if (ClickRecovery("recovery/close", -1)) SeekPhase(70); break;
            case 70:
                Require(!Game.IsRecoveryWorkspaceRestorePending && Game.RecoveryWorkspaceRestoreFailure == null,
                    "Dismissal must retain the completed actual workspace restoration");
                var state = Game.CaptureRecoveryWorkspaceVerification();
                if (state.Documents.Length != receipt!.Workspace.Documents.Length) break;
                Require(JsonSerializer.Serialize(state.Documents.OrderBy(document => document.Path, StringComparer.OrdinalIgnoreCase))
                    == JsonSerializer.Serialize(receipt.Workspace.Documents.OrderBy(document => document.Path, StringComparer.OrdinalIgnoreCase)),
                    "Actual reopened clean and dirty documents must retain full native asset/package/editor descriptors");
                Require(JsonSerializer.Serialize(state.Groups) == JsonSerializer.Serialize(receipt.Workspace.Groups)
                    && Same(state.ActivePath!, receipt.Workspace.ActivePath!),
                    "Recovered canonical group membership/order/active paths must match the durable native workspace");
                if (!Game.VerifyRecoveryAcceptanceNativeFocus(receipt.Workspace.ActivePath!, out restoredFocus)) break;
                nativeIniComparison = new { actual = state.NativeIni, expected = receipt.Workspace.NativeIni,
                    actualDocking = DockingIni(state.NativeIni), expectedDocking = DockingIni(receipt.Workspace.NativeIni),
                    liveRoot = Game.InspectRecoveryAcceptanceDockGeometry(receipt.Workspace.ActivePath!) };
                Require(DockingIni(state.NativeIni) == DockingIni(receipt.Workspace.NativeIni),
                    "The actual native docking INI topology must be reapplied from the crash checkpoint");
                dirty = Game.FindRecoveryAcceptanceDocument(receipt.DirtyPath);
                first = Game.FindRecoveryAcceptanceDocument(receipt.NativeDocks.First(item => !Same(item.Path, receipt.DirtyPath)).Path);
                Require(first != null && !first.IsDirty && dirty != null && !dirty.IsDirty,
                    "Workspace-only recovery must reopen both native owners without silently applying dirty authored payloads");
                dirtyEntity = receipt.DirtyEntity; AssertOriginalDisks();
                var paneEvidence = new List<object>();
                foreach (var expected in receipt.NativeDocks)
                {
                    var owner = Game.FindRecoveryAcceptanceDocument(expected.Path)!;
                    var actual = Game.InspectDocumentGroupParity(Game.VerificationDocumentId(owner)!.Value);
                    if (actual is not { Runtime: not null }) return;
                    var durableGroup = receipt.Workspace.Groups.Groups.Single(group => group.Id == expected.GroupId);
                    var expectedVisible = durableGroup.ActivePath != null && Same(durableGroup.ActivePath, expected.Path);
                    Require(actual.Visible == expectedVisible,
                        "Restored scene visibility must equal its durable pane-local active owner, including the scene hidden by the selected linked source tab");
                    Require(actual.GroupId == expected.GroupId && actual.DockId == expected.DockId
                        && actual.ParentDockId == expected.ParentDockId && actual.ParentSplitAxis == expected.Axis,
                        "Both restored scenes must retain actual native worlds and original split group/dock/axis identities");
                    if (expectedVisible)
                    {
                        if (!restoredSceneRenderBaselines.TryGetValue(expected.Path, out var baseline))
                        { restoredSceneRenderBaselines.Add(expected.Path, actual.RenderRevision); return; }
                        if (actual.RenderRevision <= baseline) return;
                    }
                    else if (durableGroup.ActivePath != null)
                    {
                        if (!Game.VerifyRecoveryAcceptanceNativeTab(durableGroup.ActivePath, out var sourcePane)) return;
                        paneEvidence.Add(sourcePane!);
                    }
                    paneEvidence.Add(new { actual.Entry.Path, expectedVisible, actual.Visible, nativeWorldRetained = actual.Runtime != null,
                        actual.GroupId, actual.DockId, actual.ParentDockId, actual.ParentSplitAxis, actual.RenderRevision });
                }
                restoredPaneEvidence = paneEvidence;
                if (++stable < 3) break;
                if (generatedReadOnlySources) protectedSourceOwnership = missingGeneratedDirtyProducer
                    ? VerifyMissingGeneratedSavedWorkspaceProjection() : Game.VerifyRecoveryGeneratedReadOnlySources(generatedReadOnlyReceipt!);
                Game.VerifyRestoredRecoverySourceMembership(receipt.Sources.Where(item => !Same(item.Membership.Descriptor.Path, includedSource)).Select(item => item.Membership).ToArray());
                foreach (var item in receipt.Sources)
                {
                    var cleanOwner = Game.FindRecoverySourceVerificationDocument(item.Membership.Descriptor.Path)
                        ?? throw new InvalidOperationException("Clean reopened native source owner is absent.");
                    Require(cleanOwner is { IsDirty: false, CanSave: true }, "Workspace-only source reopening must remain clean and genuinely editable before separate payload consent");
                    var entryId = Game.InspectRecoverySourceInput(cleanOwner.Path)!.Value.EntryId;
                    cleanSourceGenerations[cleanOwner.Path] = Game.InspectDocumentGroupParity(entryId)?.Generation;
                    Require(cleanSourceGenerations[cleanOwner.Path] != null, "Clean reopened source must have a real published canonical generation");
                }
                preDataRestoreGeneration = Game.InspectDocumentGroupParity(Game.VerificationDocumentId(dirty!)!.Value)?.Generation;
                Require(preDataRestoreGeneration != null, "The reopened clean owner must have an actual published generation before payload consent");
                SeekPhase(71); break;
            case 71: if (Click("menu/file")) Next(); break;
            case 72: if (Click("menu/file/recover-changes")) SeekPhase(6); break;
            case 80:
                var compensated = Game.CaptureRecoveryWorkspaceVerification();
                Require(compensationBaseline != null && JsonSerializer.Serialize(compensated.Documents) == JsonSerializer.Serialize(compensationBaseline.Documents)
                    && JsonSerializer.Serialize(compensated.Groups) == JsonSerializer.Serialize(compensationBaseline.Groups)
                    && Same(compensated.ActivePath!, compensationBaseline.ActivePath!),
                    "Actual recovery failure compensation must restore all original canonical documents, group membership and active owner without added views");
                if (!compensationDocumentFocusVerified)
                {
                    if (!Game.VerifyRecoveryAcceptancePriorNativeFocus(compensationBaseline!.ActivePath!, compensationRestoreClickFrame, out restoredFocus)) break;
                    compensationDocumentFocusVerified = true;
                    using var acknowledged = JsonDocument.Parse(JsonSerializer.Serialize(restoredFocus));
                    compensationDocumentFocusFrame = acknowledged.RootElement.GetProperty("ackFrame").GetInt32();
                }
                Require(Game.VerifyRecoveryAcceptanceNativeTab(compensationBaseline!.ActivePath!, out _),
                    "The original canonical document must remain the selected native document tab when the error tool takes keyboard focus");
                nativeIniComparison = new { actual = compensated.NativeIni, expected = compensationBaseline.NativeIni,
                    actualDocking = DockingIni(compensated.NativeIni), expectedDocking = DockingIni(compensationBaseline.NativeIni),
                    liveRoot = Game.InspectRecoveryAcceptanceDockGeometry(compensationBaseline.ActivePath!) };
                if (Game.InspectRecoveryCompensationOutputActivation() is not { } activation) break;
                if (!Game.VerifyRecoveryAcceptanceOutputNativeFocus(out compensationOutputFocus)) break;
                expectedOutputActivation = activation.Evidence;
                Require(DockingIni(compensated.NativeIni) == ExpectedCompensationDocking(compensationBaseline.NativeIni, compensated.NativeIni, activation.DockId, activation.TabId),
                    "Failure compensation must preserve complete original native docking INI, allowing only the actual controlled-error Output reveal's selected token on its proven native node");
                Require(Game.CaptureRecoveryAcceptanceHistory() == compensationHistory,
                    "Recovery preparation failure must retain exact original history transactions, position and savepoints");
                foreach (var item in compensationDisks) Require(Hash(item.Path) == item.Sha256,
                    "Recovery compensation must preserve exact original private scene/project/source and externally changed disk bytes");
                compensationOwnerProof = Game.VerifyRecoveryWorkspaceCompensation();
                if (dirtyRetainedOwner)
                {
                    if (!RetainedCompensationDataEquals(EditedX)) break;
                    Require(retainedDirtyScene!.IsDirty && ReferenceEquals(Game.FindRecoveryAcceptanceDocument(retainedDirtyScene.Path), retainedDirtyScene)
                        && Game.InspectDocumentGroupParity(retainedDirtyEntry)?.Generation == retainedDirtyGeneration,
                        "Failure compensation must preserve the exact already-dirty original canonical scene generation and authored/runtime data");
                    dirtyOwnerCompensationProof = new { retainedDirtyOwnerVerified = true, nonzeroHistoryVerified = true,
                        nativeDataPreserved = true, runtimePreserved = true, retainedDirtyEntry, retainedDirtyGeneration,
                        retainedDirtyEntity, ownedDirtyTransaction, history = retainedDirtyScene.History };
                    SeekPhase(110); break;
                }
                using (var proof = JsonDocument.Parse(JsonSerializer.Serialize(compensationOwnerProof)))
                {
                    Require(proof.RootElement.GetProperty("actualNativeRetirementCompleted").GetBoolean()
                        && proof.RootElement.GetProperty("scenarioPassed").GetBoolean(),
                        "Actual added scene generation must retire through native compensation before workflow completion");
                    recoveryCompensation = new { actualNativeRetirementCompleted = true, priorOwnerIdentityPreserved = true,
                        priorGroupsRestored = true, priorHistoryPreserved = true, originalDiskBytesPreserved = true,
                        transitionDrained = !Game.IsRecoveryWorkspaceRestorePending,
                        originalFailure = proof.RootElement.GetProperty("originalFailure").GetString(), actual = compensationOwnerProof };
                }
                Finish(true, "E08.recovery.compensation PASS: actual post-ready added scene failure, exact original canonical owners/history/layout/focus/disk preserved, added native scene CPU/GPU retirement completed; outer terminal remains required.");
                break;
            case 90: if (ClickRecovery("recovery/close", -1)) Next(); break;
            case 91:
                retainedDirtyScene ??= Game.Document ?? throw new InvalidOperationException("Original dirty-owner fixture scene is absent.");
                retainedDirtyEntry = Game.VerificationDocumentId(retainedDirtyScene)!.Value;
                retainedDirtyGeneration = Game.InspectDocumentGroupParity(retainedDirtyEntry)?.Generation ?? throw new InvalidOperationException("Original native generation is absent.");
                if (Click("entity/Imported cube")) Next(); break;
            case 92:
                if (retainedDirtyScene!.Selected?.Name != "Imported cube") break;
                retainedDirtyEntity = retainedDirtyScene.Selected.Id;
                retainedOriginalX = retainedDirtyScene.Selected.Transform.Position.X;
                retainedCleanHistory = retainedDirtyScene.History;
                Require(!retainedDirtyScene.IsDirty && retainedOriginalX != EditedX, "Dirty compensation must start from different clean native data");
                if (ClickInspector("Position/0")) Next(); break;
            case 93: SelectAll(true); Next(); break;
            case 94: SelectAll(false); Type(EditedX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); Next(); break;
            case 95: Gui.GetIO().AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 96: Gui.GetIO().AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
            case 97: if (Click("entity/Imported cube")) Next(); break;
            case 98:
                if (!RetainedCompensationDataEquals(EditedX)) break;
                var editedHistory = retainedDirtyScene!.History;
                Require(retainedDirtyScene.IsDirty && editedHistory.Position == retainedCleanHistory!.Position + 1
                    && editedHistory.Entries.Count == retainedCleanHistory.Entries.Count + 1
                    && editedHistory.Entries.Take(retainedCleanHistory.Entries.Count).SequenceEqual(retainedCleanHistory.Entries),
                    "Actual native property gesture must create one owned nonzero history transaction while preserving prior entries");
                ownedDirtyTransaction = editedHistory.Entries[editedHistory.Position - 1].Id;
                Require(ownedDirtyTransaction != Guid.Empty && editedHistory.Entries[editedHistory.Position - 1].IsDone,
                    "Dirty retained-owner acceptance requires a genuinely applied native transaction");
                AssertOriginalDisks(); dirtyOwnerPrepared = true; Next(); break;
            case 99: if (Click("menu/file")) Next(); break;
            case 100: if (Click("menu/file/recover-changes")) SeekPhase(2); break;
            case 110:
                Require(retainedDirtyScene!.History.Entries[retainedDirtyScene.History.Position - 1].Id == ownedDirtyTransaction,
                    "Cleanup may undo only the original owned native transaction after exact compensation history preservation");
                if (Click("toolbar/undo")) Next(); break;
            case 111:
                if (!RetainedCompensationDataEquals(retainedOriginalX) || retainedDirtyScene!.IsDirty) break;
                var undoneHistory = retainedDirtyScene.History;
                Require(undoneHistory.Position == retainedCleanHistory!.Position
                    && undoneHistory.Entries.Count == retainedCleanHistory.Entries.Count + 1
                    && undoneHistory.Entries.Take(retainedCleanHistory.Entries.Count).SequenceEqual(retainedCleanHistory.Entries)
                    && undoneHistory.Entries[undoneHistory.Position].Id == ownedDirtyTransaction
                    && !undoneHistory.Entries[undoneHistory.Position].IsDone
                    && Game.InspectDocumentGroupParity(retainedDirtyEntry)?.Generation == retainedDirtyGeneration,
                    "Actual toolbar Undo must clean only the owned edit, retaining original generation and an exact redo transaction");
                foreach (var item in compensationDisks) Require(Hash(item.Path) == item.Sha256, "Dirty compensation cleanup must retain all original private disk bytes");
                using (var ownerProof = JsonDocument.Parse(JsonSerializer.Serialize(compensationOwnerProof)))
                    recoveryCompensation = new { actualNativeRetirementCompleted = ownerProof.RootElement.GetProperty("actualNativeRetirementCompleted").GetBoolean(),
                        priorOwnerIdentityPreserved = true, priorGroupsRestored = true, priorHistoryPreserved = true, originalDiskBytesPreserved = true,
                        transitionDrained = !Game.IsRecoveryWorkspaceRestorePending, originalFailure = ownerProof.RootElement.GetProperty("originalFailure").GetString(),
                        retainedOriginalDirty = true, historyNonzero = true, retainedDirtyOwnerVerified = true, nonzeroHistoryVerified = true,
                        nativeDataPreserved = true, runtimePreserved = true, ownedTransactionUndone = true,
                        actual = compensationOwnerProof, dirtyOwner = dirtyOwnerCompensationProof, cleanupHistory = undoneHistory };
                Finish(true, "E08.recovery.compensation dirty PASS: actual nonzero-history native property edit retained through controlled added-scene failure and retirement; exact owner/data/runtime/history/bytes preserved, own gesture undone through normal toolbar before required terminal cleanup.");
                break;
            case 6:
                if (ClickRecovery("recovery/document/" + receipt!.SessionId + "/" + receipt.DirtyPayloadHash, 1)) Next(); break;
            case 7:
                dirty = Game.FindRecoveryAcceptanceDocument(receipt!.DirtyPath);
                if (dirty is not { IsDirty: true }) break;
                var recoveredView = Game.InspectDocumentGroupParity(Game.VerificationDocumentId(dirty)!.Value);
                if (recoveredView is not { Visible: true, Runtime: not null } || recoveredView.RenderRevision <= 0) break;
                Require(recoveredView.Generation != null && recoveredView.Generation != preDataRestoreGeneration,
                    "Actual document Restore control must publish a new canonical generation for the dirty payload");
                AssertDirtyData(); AssertOriginalDisks();
                var afterPayload = Game.CaptureRecoveryWorkspaceVerification();
                Require(JsonSerializer.Serialize(afterPayload.Groups) == JsonSerializer.Serialize(receipt.Workspace.Groups)
                    && Same(afterPayload.ActivePath!, receipt.Workspace.ActivePath!)
                    && DockingIni(afterPayload.NativeIni) == DockingIni(receipt.Workspace.NativeIni),
                    "Separate dirty-payload consent must preserve the restored native group/active/docking presentation");
                restored = true; Next(); break;
            case 8: Next(); break;
            case 9: sourceIndex = 0; SeekPhase(missingGeneratedDirtyProducer ? 160 : 40); break;
            case 40:
                // Consent separately to each real source payload; workspace recovery never silently applies edits.
                if (ClickRecovery("recovery/document/" + receipt!.SessionId + "/" + receipt.Sources[sourceIndex].PayloadHash, -1)) Next(); break;
            case 41:
                var expectedSource = receipt!.Sources[sourceIndex];
                var source = Game.FindRecoverySourceVerificationDocument(expectedSource.Membership.Descriptor.Path);
                if (source?.IsDirty != true || source.Text != expectedSource.EditedText) break;
                var sourceEntryId = Game.InspectRecoverySourceInput(source.Path)!.Value.EntryId;
                Require(Game.InspectDocumentGroupParity(sourceEntryId)?.Generation is { } sourceGeneration
                    && sourceGeneration != cleanSourceGenerations[source.Path], "Separate source payload Restore must publish a genuinely new canonical native source generation");
                Game.VerifyRestoredRecoverySourceMembership([expectedSource.Membership], source.Path, expectedSource.EditedText,
                    Same(source.Path, includedSource) ? externalSourceHash : expectedSource.Membership.DiskSha256);
                if (++sourceIndex < receipt.Sources.Length) SeekPhase(40); else Next(); break;
            case 42:
                if (ClickRecovery("recovery/close", -1)) { protectedSourceIndex = 0; SeekPhase(generatedReadOnlySources ? 120 : 43); }
                break;
            case 43:
                var includedInput = Game.InspectRecoverySourceInput(includedSource);
                if (includedInput != null && Click("document-tab/" + includedInput.Value.EntryId)) Next(); break;
            case 44:
                var includedOwner = Game.FindRecoverySourceVerificationDocument(includedSource)!;
                sourceVersionBeforeSave = includedOwner.Version;
                sourceOwnerBeforeSave = includedOwner;
                sourceHistoryBeforeSave = JsonSerializer.Serialize(includedOwner.History);
                var includedEntry = Game.InspectRecoverySourceInput(includedSource)!.Value.EntryId;
                sourceGenerationBeforeSave = Game.InspectDocumentGroupParity(includedEntry)?.Generation;
                if (Game.Controls.Centers.TryGetValue("document-tab/" + includedEntry, out var tabPoint) && Aim("recovery-source-close", tabPoint))
                { Gui.GetIO().AddMouseButtonEvent(1, true); SeekPhase(50); } break;
            case 50: Gui.GetIO().AddMouseButtonEvent(1, false); Next(); break;
            case 51:
                var closingEntry = Game.InspectRecoverySourceInput(includedSource)!.Value.EntryId;
                if (Click("document-close/" + closingEntry + "/close")) SeekPhase(45); break;
            case 45: if (Click("document-close/save")) Next(); break;
            case 46:
                saveFailure = Game.StatusMessage;
                if (saveFailure != "The authored file changed on disk. Reload it before saving.") break;
                var retainedSource = Game.FindRecoverySourceVerificationDocument(includedSource)!;
                Require(ReferenceEquals(retainedSource, sourceOwnerBeforeSave) && retainedSource.CanSave && retainedSource.IsDirty
                    && retainedSource.Version == sourceVersionBeforeSave
                    && JsonSerializer.Serialize(retainedSource.History) == sourceHistoryBeforeSave
                    && retainedSource.Text == receipt!.Sources.Single(item => Same(item.Membership.Descriptor.Path, includedSource)).EditedText
                    && Game.InspectDocumentGroupParity(Game.InspectRecoverySourceInput(includedSource)!.Value.EntryId)?.Generation == sourceGenerationBeforeSave
                    && Hash(includedSource) == externalSourceHash,
                    "The actual native Save-and-close command must reject the external source conflict, retaining dirty text/history and exact external disk bytes");
                if (Click("document-close/cancel")) Next(); break;
            case 47:
                AssertDirtyData(); AssertOriginalDisks();
                foreach (var item in receipt!.Sources)
                    Game.VerifyRestoredRecoverySourceMembership([item.Membership], item.Membership.Descriptor.Path, item.EditedText,
                        Same(item.Membership.Descriptor.Path, includedSource) ? externalSourceHash : item.Membership.DiskSha256);
                if (generatedReadOnlySources) protectedSourceOwnership = missingGeneratedDirtyProducer
                    ? VerifyMissingGeneratedRecoveredProjection() : Game.VerifyRecoveryGeneratedReadOnlySources(generatedReadOnlyReceipt!);
                Require(Hash(includedSource) == externalSourceHash, "Cancel after conflict must preserve the external source disk generation");
                Finish(true, "E08.recovery.read PASS: owned crash, actual workspace and separate scene/included/linked source payload controls, Unicode native source edits, original retained source baselines and normal Save-and-close conflict guard; unchanged scene/linked/external bytes and required terminal retirement.");
                break;
        }
    }
    private void AssertDirtyData()
    {
        var owner = dirty ?? throw new InvalidOperationException("Dirty recovery owner missing");
        var entity = owner.Asset.Hierarchy.Parts[dirtyEntity].Entity;
        Require(owner.IsDirty && entity.Transform.Position.X == EditedX, "Real authored dirty transform must equal the actual widget edit");
        var field = owner.GetComponentFields(entity.Transform).Single(item => item.Name == "Position");
        Require(Game.VerificationDocumentViewport(owner)?.ReadRuntimeComponentValueVerification(field) is Vector3 runtime && runtime.X == EditedX,
            "Recovered authored transform must synchronize into the actual native runtime component");
    }
    private void AssertOriginalDisks()
    {
        foreach (var item in producer ? disks : receipt!.OriginalDisks)
        {
            if (!producer && missingGeneratedDirtyProducer && missingPreRestoreValidated && Same(item.Path, generatedReadOnlyReceipt!.GeneratedPath))
            { Require(!File.Exists(item.Path) && !Directory.Exists(item.Path), "Only the certified missing compiler artifact may differ from original authored disk receipts"); continue; }
            Require(Hash(item.Path) == item.Sha256, "Recovery journal/restoration must not replace original authored scene bytes");
        }
    }
    private bool ClickInspector(string id)
    {
        if (Game.Controls.Centers.ContainsKey(id)) return Click(id);
        if (Game.InspectWorkflowToolBody("editor.properties") is not { Selected: true } body) { Click("tooltab/editor.properties"); return false; }
        Gui.GetIO().AddMousePosEvent(body.Center.X, body.Center.Y); Gui.GetIO().AddMouseWheelEvent(0, -1); return false;
    }
    private bool ClickRecovery(string id, float direction)
    {
        if (Game.Controls.Centers.ContainsKey(id)) return Click(id);
        if (Game.InspectRecoveryAcceptanceModal() is not { } body || Clock.Elapsed.TotalSeconds < nextRecoveryWheel) return false;
        if (!Game.Controls.Centers.TryGetValue("recovery/body", out var center)
            || !Game.Controls.Viewports.ContainsKey("recovery/body")) return false;
        if (recoveryScrollTarget != id) { recoveryScrollTarget = id; recoveryScrollDirection = direction; }
        if (recoveryScrollDirection > 0 && body.ScrollY <= .5f) recoveryScrollDirection = -1;
        else if (recoveryScrollDirection < 0 && body.ScrollY >= body.ScrollMaxY - .5f) recoveryScrollDirection = 1;
        if (!Aim("recovery/body", center)) return false;
        Gui.GetIO().AddMouseWheelEvent(0, recoveryScrollDirection);
        recoveryWheelEvents++; nextRecoveryWheel = Clock.Elapsed.TotalSeconds + .05;
        return false;
    }
    private bool PrepareCompensationBaselineReadiness()
    {
        var current = Game.CaptureRecoveryWorkspaceVerification();
        if (current.ActivePath == null || !Game.IsRecoveryAcceptanceNativeBaselineReady(current.ActivePath, out compensationBaselineReadiness))
        { compensationBaselineStableFrames = 0; compensationBaselineStableKey = null; return false; }
        var owner = Game.FindRecoveryAcceptanceDocument(current.ActivePath);
        var view = owner == null ? null : Game.InspectDocumentGroupParity(Game.VerificationDocumentId(owner)!.Value);
        if (view is not { Visible: true, Runtime: not null } || view.RenderRevision <= 0)
        { compensationBaselineStableFrames = 0; return false; }
        compensationBaselineFirstRender ??= view.RenderRevision;
        if (view.RenderRevision <= compensationBaselineFirstRender.Value) return false;
        var key = JsonSerializer.Serialize(new { current.Documents, current.Groups, current.ActivePath,
            docking = DockingIni(current.NativeIni), history = Game.CaptureRecoveryAcceptanceHistory() });
        if (compensationBaselineStableKey == key) compensationBaselineStableFrames++;
        else { compensationBaselineStableKey = key; compensationBaselineStableFrames = 1; }
        return compensationBaselineStableFrames >= 3;
    }
    private bool RetainedCompensationDataEquals(float expectedX)
    {
        if (retainedDirtyScene == null || !retainedDirtyScene.Asset.Hierarchy.Parts.TryGetValue(retainedDirtyEntity, out var part)) return false;
        var entity = part.Entity;
        if (entity.Transform.Position.X != expectedX) return false;
        var field = retainedDirtyScene.GetComponentFields(entity.Transform).Single(item => item.Name == "Position");
        return Game.VerificationDocumentViewport(retainedDirtyScene)?.ReadRuntimeComponentValueVerification(field) is Vector3 runtime && runtime.X == expectedX;
    }
    private void StepProtectedSourceTyping(int firstPhase, int completedPhase)
    {
        var expected = generatedReadOnlyReceipt ?? throw new InvalidOperationException("Protected source provenance receipt is absent.");
        var path = protectedSourceIndex switch { 0 => expected.GeneratedPath, 1 => expected.ReadOnlyPath, 2 => expected.ExternalReadOnlyPath,
            _ => throw new InvalidOperationException("The exact protected source fixture has three native presentations.") };
        if (protectedAttemptDeadlinePath != path)
        {
            protectedAttemptDeadlinePath = path;
            protectedAttemptDeadline = Clock.Elapsed.TotalSeconds + 30;
            protectedTabClickAttempts = 0;
            protectedScrollClickAttempts = 0;
        }
        if (Clock.Elapsed.TotalSeconds >= protectedAttemptDeadline)
            throw new TimeoutException("Protected native source navigation/input exceeded its shared 30-second deadline across all phases and actual click retries: " + path);
        var input = Game.InspectRecoverySourceInput(path);
        var io = Gui.GetIO();
        if (releaseProtectedScrollClick) { io.AddMouseButtonEvent(0, false); releaseProtectedScrollClick = false; return; }
        switch (Phase - firstPhase)
        {
            case 0:
                if (!Game.IsRecoveryProtectedNativeNavigationReady(out protectedNavigationReadiness)) break;
                if (protectedTabClickAttempts >= 8)
                    throw new InvalidOperationException("Protected native source did not become selected after eight genuine tab clicks: " + path);
                if (input != null && !Game.Controls.Centers.ContainsKey("document-tab/" + input.Value.EntryId))
                {
                    var arrow = Game.InspectRecoveryProtectedTabScroll(path);
                    if (arrow == null) break;
                    if (protectedScrollClickAttempts >= 16) throw new InvalidOperationException("Protected tab remained clipped after sixteen genuine native scrolling-button clicks: " + path);
                    if (!Game.Controls.Centers.TryGetValue(arrow.Value.Id, out var arrowCenter)
                        || !Game.Controls.NativeIDs.TryGetValue(arrow.Value.Id, out var nativeId)
                        || Gui.GetFrameCount() - Game.Controls.NativeFrames.GetValueOrDefault(arrow.Value.Id, -1) is not (0 or 1)) break;
                    if (!Aim(arrow.Value.Id, arrowCenter)) break;
                    protectedScrollProof = new { arrow.Value.Id, nativeId, hoveredId = Gui.GetCurrentContext().HoveredId, arrow.Value.Evidence };
                    if (Gui.GetCurrentContext().HoveredId != nativeId) break;
                    io.AddMouseButtonEvent(0, true); releaseProtectedScrollClick = true; protectedScrollClickAttempts++;
                    break;
                }
                if (input != null && Click("document-tab/" + input.Value.EntryId))
                {
                    protectedTabClickAttempts++;
                    Next();
                }
                break;
            case 1:
                if (input?.Center is not { } center)
                {
                    if (Game.IsRecoveryProtectedNativeNavigationReady(out protectedNavigationReadiness)
                        && !Game.VerifyRecoveryAcceptanceNativeTab(path, out _)) SeekPhase(firstPhase);
                    break;
                }
                var current = Game.FindRecoverySourceVerificationDocument(path)!;
                Require(!current.CanEditText && (!current.IsDirty || missingGeneratedDirtyProducer && protectedSourceIndex == 0),
                    "Attempted protected-source typing requires the actual read-only presentation; a missing compiler-owned projection may retain its native pending-output dirty state");
                protectedSourceDirtyBaseline = current.IsDirty;
                protectedSourceVersion = current.Version; protectedSourceText = current.Text;
                protectedSourceHistory = JsonSerializer.Serialize(current.History);
                protectedSourceGeneration = Game.InspectDocumentGroupParity(input.Value.EntryId)?.Generation;
                Require(protectedSourceGeneration != null, "Protected native text surface must have an actual canonical generation");
                if (Aim("document-tab/" + input.Value.EntryId, center)) { io.AddMouseButtonEvent(0, true); Next(); }
                break;
            case 2: io.AddMouseButtonEvent(0, false); Next(); break;
            case 3:
                if (input?.InputActive != true) break;
                if (!Game.VerifyRecoveryAcceptanceNativeFocus(path, out var beforeTypingFocus)) break;
                Type("Must not edit: Ω 日本語 🚀");
                protectedTypingProofs.Add(new { stage = "actual-native-input-attempt", producer, path, beforeTypingFocus,
                    actualInputActive = true, canEditText = Game.FindRecoverySourceVerificationDocument(path)!.CanEditText,
                    generated = protectedSourceIndex == 0 });
                Next(); break;
            case 4:
                var after = Game.FindRecoverySourceVerificationDocument(path)!;
                var activeInput = input ?? throw new InvalidOperationException("The focused protected native text surface disappeared after input.");
                Require(activeInput.InputActive && !after.CanEditText && after.IsDirty == protectedSourceDirtyBaseline
                    && after.Version == protectedSourceVersion && after.Text == protectedSourceText
                    && JsonSerializer.Serialize(after.History) == protectedSourceHistory
                    && Game.InspectDocumentGroupParity(activeInput.EntryId)?.Generation == protectedSourceGeneration,
                    "Real focused native Unicode typing on protected source must not alter text, version, dirty state, canonical generation or native history");
                protectedSourceOwnership = missingGeneratedDirtyProducer && !producer
                    ? VerifyMissingGeneratedRecoveredProjection() : Game.VerifyRecoveryGeneratedReadOnlySources(expected);
                protectedTypingProofs.Add(new { stage = "zero-mutation-after-native-input", producer, path,
                    textUnchanged = true, versionUnchanged = true, historyUnchanged = true, generationUnchanged = true,
                    originalNativeFilesUnchanged = true, generatedCanSave = after.CanSave, after.CanEditText,
                    actualInputActive = activeInput.InputActive });
                if (++protectedSourceIndex < 3) SeekPhase(firstPhase); else SeekPhase(completedPhase);
                break;
        }
    }
    private object? DescribeRecoveryModal()
        => Game.InspectRecoveryAcceptanceModal() is { } body ? new
        {
            centerX = body.Center.X, centerY = body.Center.Y, body.ScrollY, body.ScrollMaxY, body.Width, body.Height,
            recordedBodyViewport = Game.Controls.Viewports.GetValueOrDefault("recovery/body"),
        } : null;
    private void AtomicReceipt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        var temp = receiptPath + ".pending";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, receipt); stream.Flush(true); }
        File.Move(temp, receiptPath);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool Same(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static string DockingIni(string ini)
    {
        var found = false; var lines = new List<string>();
        foreach (var line in ini.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line == "[Docking][Data]") { found = true; continue; }
            if (found && line.StartsWith('[')) break;
            if (found && !string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
        }
        Require(lines.Count > 0, "Recovery checkpoint must contain actual native docking data");
        return string.Join("\n", lines);
    }
    private static string ExpectedCompensationDocking(string expectedIni, string actualIni, uint outputDockId, uint outputTabId)
    {
        var expected = DockingIni(expectedIni).Split('\n');
        var actual = DockingIni(actualIni).Split('\n');
        var pattern = @"(?:^|\s)ID=0x([0-9A-Fa-f]+)(?:\s|$)";
        bool OutputNode(string line)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, pattern);
            return match.Success && uint.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture) == outputDockId;
        }
        var actualLine = actual.Single(OutputNode);
        var actualSelection = System.Text.RegularExpressions.Regex.Match(actualLine, @"\bSelected=0x([0-9A-Fa-f]+)\b");
        Require(actualSelection.Success && uint.Parse(actualSelection.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture) == outputTabId,
            "The current persisted Output node selection must equal the proven selected/visible native Output tab");
        var index = Array.FindIndex(expected, OutputNode);
        Require(index >= 0 && expected.Count(OutputNode) == 1
            && System.Text.RegularExpressions.Regex.IsMatch(expected[index], @"\bSelected=0x[0-9A-Fa-f]+\b"),
            "Only the pre-existing selected token on the exact native Output dock node may change after error reveal");
        expected[index] = System.Text.RegularExpressions.Regex.Replace(expected[index], @"\bSelected=0x[0-9A-Fa-f]+\b", actualSelection.Value);
        return string.Join("\n", expected);
    }
    private static void SelectAll(bool down) { Gui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, down); Gui.GetIO().AddKeyEvent(ImGuiKey.A, down); }
    private static void Type(string value) { foreach (var rune in value.EnumerateRunes()) Gui.GetIO().AddInputCharacter((uint)rune.Value); }
    protected override void CleanInput() { SelectAll(false); Gui.GetIO().AddKeyEvent(ImGuiKey.End, false); Gui.GetIO().AddKeyEvent(ImGuiKey.Enter, false); Gui.GetIO().AddMouseButtonEvent(0, false); Gui.GetIO().AddMouseButtonEvent(1, false); }
}

internal sealed partial class EditorGame
{
    internal unsafe (string Id, object Evidence)? InspectRecoveryProtectedTabScroll(string path)
    {
        var entry = documents.Find(path);
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        var tabs = group == null ? null : DocumentGroupPanel.InspectNativeTabs(group.Id);
        if (entry == null || tabs == null) return null;
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(tabs));
        var barId = snapshot.RootElement.GetProperty("currentBarId").GetUInt32();
        var item = snapshot.RootElement.GetProperty("documentTabs").EnumerateArray().FirstOrDefault(tab => tab.GetProperty("document").GetGuid() == entry.Id);
        if (item.ValueKind != JsonValueKind.Object) return null;
        var tabId = item.GetProperty("nativeTab").GetUInt32();
        var bars = Gui.GetCurrentContext().TabBars.Buf;
        for (var index = 0; index < bars.Size; index++)
        {
            var bar = new ImGuiTabBarPtr(bars.Data + index);
            if (bar.ID != barId || !bar.ScrollButtonEnabled || Gui.GetFrameCount() - bar.CurrFrameVisible is not (0 or 1)) continue;
            var target = ImGuiP.TabBarFindTabByID(bar, tabId);
            var selected = ImGuiP.TabBarFindTabByID(bar, bar.SelectedTabId);
            if (target.IsNull || selected.IsNull) return null;
            var right = target.IndexDuringLayout >= selected.IndexDuringLayout;
            var id = "document-tab-scroll/" + barId + (right ? "/right" : "/left");
            return (id, new { barId, targetTabId = tabId, selectedTabId = bar.SelectedTabId,
                targetOrder = target.IndexDuringLayout, selectedOrder = selected.IndexDuringLayout,
                bar.ScrollingRectMinX, bar.ScrollingRectMaxX, bar.ScrollingAnim, bar.ScrollingTarget,
                direction = right ? "right" : "left", actualFrame = bar.CurrFrameVisible });
        }
        return null;
    }
    internal bool IsRecoveryProtectedNativeNavigationReady(out object evidence)
    {
        var context = Gui.GetCurrentContext();
        var io = Gui.GetIO();
        var popupCount = context.OpenPopupStack.Size;
        var heldMouse = io.MouseDown[0] || io.MouseDown[1] || io.MouseDown[2];
        var dragging = context.DragDropActive || WindowsSourceTextDragDrop.IsDragging;
        evidence = new { frame = Gui.GetFrameCount(), NativeDocumentRestorationSettled, popupCount, heldMouse, dragging,
            actualActiveId = ImGuiP.GetActiveID(), navWindowId = context.NavWindow.IsNull ? 0u : context.NavWindow.ID,
            nativeSelectionRestore = InspectNativeDockSelectionRestore() };
        return NativeDocumentRestorationSettled && popupCount == 0 && !heldMouse && !dragging;
    }
    internal unsafe bool VerifyRecoveryAcceptancePriorNativeFocus(string path, int minimumFrame, out object? evidence)
    {
        var entry = documents.Entries.SingleOrDefault(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        if (entry == null || group == null || minimumFrame < 0) { evidence = null; return false; }
        var window = ImGuiP.FindWindowByName(group.WindowName);
        var tabs = DocumentGroupPanel.InspectNativeTabs(group.Id);
        if (window.IsNull || tabs == null) { evidence = null; return false; }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(tabs));
        var root = json.RootElement;
        var ackFrame = root.GetProperty("lastFocusAckFrame").GetInt32();
        var ackNavWindowId = root.GetProperty("lastFocusAckNavWindowId").GetUInt32();
        var popupCount = root.GetProperty("observedFocusPopupCount").GetInt32();
        var requestsSettled = root.GetProperty("RequestedFocus").ValueKind == JsonValueKind.Null
            && root.GetProperty("RequestedSelection").ValueKind == JsonValueKind.Null;
        var currentTab = VerifyRecoveryAcceptanceNativeTab(path, out var currentTabEvidence);
        var freshNativeAck = ackFrame > minimumFrame && ackFrame <= Gui.GetFrameCount() && ackNavWindowId == window.ID && popupCount == 0;
        evidence = new { entryId = entry.Id, groupId = group.Id, path, minimumFrame, ackFrame, ackNavWindowId,
            expectedPaneWindowId = window.ID, popupCount, freshNativeAck, requestsSettled, currentTab,
            canonicalActive = documents.Active?.Id, groupActive = group.Active?.Id, currentTabEvidence, tabs,
            scope = "Actual CompleteFocus acknowledgment after this Restore command; a later Output error activation may own current navigation focus." };
        return freshNativeAck && requestsSettled && currentTab && ReferenceEquals(documents.Active, entry) && ReferenceEquals(group.Active, entry);
    }
    internal unsafe bool VerifyRecoveryAcceptanceOutputNativeFocus(out object? evidence)
    {
        var window = ImGuiP.FindWindowByName(EditorPanels.Output.DisplayWindowName);
        if (window.IsNull || !window.WasActive || window.Hidden) { evidence = null; return false; }
        var focused = Gui.GetCurrentContext().NavWindow;
        var chain = new List<object>();
        var inside = false;
        for (var depth = 0; !focused.IsNull && depth < 128; depth++, focused = focused.ParentWindow)
        {
            chain.Add(new { focused.ID, name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)focused.Name) });
            if (focused.ID == window.ID) { inside = true; break; }
        }
        evidence = new { frame = Gui.GetFrameCount(), outputWindowId = window.ID, nativeFocusInsideOutput = inside,
            outputPanel.FocusRequested, canonicalActivePath = documents.Active?.Path, chain };
        return inside && !outputPanel.FocusRequested;
    }
    internal unsafe object InspectRecoveryAcceptanceToolTabs()
    {
        var result = new List<object>();
        foreach (var panel in EditorPanels.All)
        {
            var window = ImGuiP.FindWindowByName(panel.DisplayWindowName);
            if (window.IsNull) continue;
            var bar = window.DockNode.IsNull ? default : window.DockNode.TabBar;
            result.Add(new { panel.CaptureId, panel.DisplayWindowName, windowId = window.ID, window.DockId, window.TabId,
                selected = bar.IsNull ? 0u : bar.SelectedTabId, visible = bar.IsNull ? 0u : bar.VisibleTabId,
                nextSelected = bar.IsNull ? 0u : bar.NextSelectedTabId, window.LastFrameActive,
                window.Active, window.WasActive, window.Appearing, window.Hidden, window.SkipItems,
                window.DockIsActive, window.DockTabIsVisible });
        }
        var focused = Gui.GetCurrentContext().NavWindow;
        return new { currentFrame = Gui.GetFrameCount(), navWindow = focused.IsNull ? 0u : focused.ID,
            navWindowName = focused.IsNull ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)focused.Name), tools = result };
    }
    internal unsafe bool IsRecoveryAcceptanceNativeBaselineReady(string path, out object? evidence)
    {
        var workArea = InspectDocumentGroupWorkArea();
        var readyTab = VerifyRecoveryAcceptanceNativeTab(path, out var tab);
        var menu = ImGuiP.FindWindowByName("##MainMenuBar");
        var entry = documents.Entries.SingleOrDefault(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        var window = group == null ? default : ImGuiP.FindWindowByName(group.WindowName);
        var root = window.IsNull ? default : window.DockNode;
        for (var depth = 0; !root.IsNull && !root.ParentNode.IsNull && depth < 128; depth++) root = root.ParentNode;
        var viewport = Gui.GetMainViewport();
        var menuReady = !menu.IsNull && menu.WasActive && !menu.Hidden && Gui.GetFrameCount() - menu.LastFrameActive is 0 or 1;
        var geometryReady = !root.IsNull && MathF.Abs(root.Pos.X - viewport.WorkPos.X) <= 1
            && MathF.Abs(root.Pos.Y - viewport.WorkPos.Y) <= 1 && MathF.Abs(root.Size.X - viewport.WorkSize.X) <= 1
            && MathF.Abs(root.Size.Y - viewport.WorkSize.Y) <= 1;
        evidence = new { readyTab, menuReady, geometryReady, workArea, tab };
        return readyTab && menuReady && geometryReady;
    }
    internal string CaptureRecoveryAcceptanceHistory()
        => JsonSerializer.Serialize(sessionHistory?.History ?? throw new InvalidOperationException("Recovery history requires the actual loaded authoring session."));
    internal unsafe object? InspectRecoveryAcceptanceDockGeometry(string path)
    {
        var entry = documents.Entries.SingleOrDefault(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        if (group == null) return null;
        var window = ImGuiP.FindWindowByName(group.WindowName);
        if (window.IsNull || window.DockNode.IsNull) return null;
        var root = window.DockNode;
        for (var depth = 0; !root.ParentNode.IsNull && depth < 128; depth++) root = root.ParentNode;
        var viewport = Gui.GetMainViewport();
        return new { frame = Gui.GetFrameCount(), rootId = root.ID, rootX = root.Pos.X, rootY = root.Pos.Y,
            rootWidth = root.Size.X, rootHeight = root.Size.Y, paneX = window.Pos.X, paneY = window.Pos.Y,
            paneWidth = window.Size.X, paneHeight = window.Size.Y, viewportX = viewport.Pos.X, viewportY = viewport.Pos.Y,
            viewportWidth = viewport.Size.X, viewportHeight = viewport.Size.Y, workX = viewport.WorkPos.X, workY = viewport.WorkPos.Y,
            workWidth = viewport.WorkSize.X, workHeight = viewport.WorkSize.Y };
    }
    internal unsafe (System.Numerics.Vector2 Center, float ScrollY, float ScrollMaxY, float Width, float Height)? InspectRecoveryAcceptanceModal()
    {
        var window = ImGuiP.FindWindowByName("Recover changes");
        if (window.IsNull || !window.WasActive || window.Hidden || Gui.GetFrameCount() - window.LastFrameActive is not (0 or 1)) return null;
        var rect = window.InnerClipRect;
        if (rect.Max.X <= rect.Min.X || rect.Max.Y <= rect.Min.Y) return null;
        var windows = Gui.GetCurrentContext().Windows;
        for (var index = 0; index < windows.Size; index++)
        {
            var child = windows[index];
            if (child.ParentWindow.IsNull || child.ParentWindow.ID != window.ID || !child.WasActive || child.Hidden) continue;
            var name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)child.Name);
            if (name?.Contains("##RecoveryBody", StringComparison.Ordinal) != true) continue;
            return ((child.InnerClipRect.Min + child.InnerClipRect.Max) / 2, child.Scroll.Y, child.ScrollMax.Y, window.Size.X, window.Size.Y);
        }
        return ((rect.Min + rect.Max) / 2, window.Scroll.Y, window.ScrollMax.Y, window.Size.X, window.Size.Y);
    }
    internal unsafe bool VerifyRecoveryAcceptanceNativeTab(string path, out object? evidence)
    {
        var entry = documents.Entries.SingleOrDefault(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        if (entry == null || group == null) { evidence = null; return false; }
        var window = ImGuiP.FindWindowByName(group.WindowName);
        var tabs = DocumentGroupPanel.InspectNativeTabs(group.Id);
        if (window.IsNull || !window.WasActive || window.Hidden || tabs == null) { evidence = null; return false; }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(tabs));
        var nativeTab = json.RootElement.GetProperty("documentTabs").EnumerateArray().FirstOrDefault(item => item.GetProperty("document").GetGuid() == entry.Id);
        var tabId = nativeTab.ValueKind == JsonValueKind.Object ? nativeTab.GetProperty("nativeTab").GetUInt32() : 0;
        var selected = tabId != 0 && json.RootElement.GetProperty("selectedNativeTab").GetUInt32() == tabId;
        var visible = tabId != 0 && json.RootElement.GetProperty("visibleNativeTab").GetUInt32() == tabId;
        var frame = json.RootElement.GetProperty("currentFrame").GetInt32();
        var current = frame >= Gui.GetFrameCount() - 1 && frame <= Gui.GetFrameCount();
        var sourcePresented = entry.Document is not ScriptSourceDocument source || scriptEditors.ContainsKey(source);
        evidence = new { entry.Path, entryId = entry.Id, groupId = group.Id, nativeSelected = selected, nativeVisible = visible, currentFrame = current, sourcePresented, tabs };
        return ReferenceEquals(group.Active, entry) && selected && visible && current && sourcePresented;
    }
    internal unsafe bool VerifyRecoveryAcceptanceNativeFocus(string path, out object? evidence)
    {
        var entry = documents.Entries.SingleOrDefault(item => string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        if (entry == null || group == null) { evidence = null; return false; }
        var window = ImGuiP.FindWindowByName(group.WindowName);
        var tabs = DocumentGroupPanel.InspectNativeTabs(group.Id);
        if (window.IsNull || !window.WasActive || window.Hidden || tabs == null) { evidence = null; return false; }
        var focused = Gui.GetCurrentContext().NavWindow;
        var nativeFocusChain = new List<object>();
        var inside = false;
        for (var depth = 0; !focused.IsNull && depth < 128; depth++, focused = focused.ParentWindow)
        {
            nativeFocusChain.Add(new { focused.ID, name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)focused.Name), focused.Active, focused.WasActive, focused.Hidden });
            if (focused.ID == window.ID) { inside = true; break; }
        }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(tabs));
        var nativeTab = json.RootElement.GetProperty("documentTabs").EnumerateArray().FirstOrDefault(item => item.GetProperty("document").GetGuid() == entry.Id);
        var tabId = nativeTab.ValueKind == JsonValueKind.Object ? nativeTab.GetProperty("nativeTab").GetUInt32() : 0;
        var actualSelected = tabId != 0 && json.RootElement.GetProperty("selectedNativeTab").GetUInt32() == tabId;
        var actualVisible = tabId != 0 && json.RootElement.GetProperty("visibleNativeTab").GetUInt32() == tabId;
        var actualFrame = json.RootElement.GetProperty("currentFrame").GetInt32();
        var currentEvidence = actualFrame >= Gui.GetFrameCount() - 1 && actualFrame <= Gui.GetFrameCount();
        var focusAcknowledged = json.RootElement.GetProperty("RequestedFocus").ValueKind == JsonValueKind.Null;
        evidence = new { entryId = entry.Id, groupId = group.Id, canonicalActive = documents.Active?.Id, groupActive = group.Active?.Id,
            expectedWindowId = window.ID, nativeFocusChain, nativeActiveId = ImGuiP.GetActiveID(),
            nativeFocused = inside, actualSelected, actualVisible, currentEvidence, focusAcknowledged, tabs };
        return ReferenceEquals(documents.Active, entry) && ReferenceEquals(group.Active, entry) && inside && actualSelected && actualVisible && currentEvidence && focusAcknowledged;
    }
    internal object? InspectRecoveryTransformLayout() => PropertyGrid.InspectorTransformLayout;
    // Inspection only: no implicit open, publication or restore.
    internal EntityHierarchyDocument? FindRecoveryAcceptanceDocument(string path)
        => documents.Entries.FirstOrDefault(entry => string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))?.Document as EntityHierarchyDocument;
}
