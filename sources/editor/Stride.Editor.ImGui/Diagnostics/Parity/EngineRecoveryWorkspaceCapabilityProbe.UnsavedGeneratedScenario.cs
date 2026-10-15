using System.Diagnostics;
using System.Text.Json;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed record RecoveryUnsavedGeneratedAcceptanceReceipt(int Version, int ProcessId, long ProcessStartedUtcTicks,
    string ProjectPath, string ManifestPath, string ManifestSha256, Guid SessionId,
    EditorRecoveryWorkspaceState Workspace, RecoveryUnsavedGeneratedProducer Producer,
    string ProducerPayloadHash, RecoveryWorkspaceDiskReceipt[] OriginalDisks);

internal sealed partial class EngineRecoveryWorkspaceCapabilityProbe
{
    private readonly bool unsavedGeneratedProducer;
    private RecoveryUnsavedGeneratedAcceptanceReceipt? unsavedReceipt;
    private RecoveryUnsavedGeneratedProducer? unsavedProducer;
    private RecoveryUnsavedGeneratedState? unsavedProducerState, unsavedConsentedState, unsavedRestoredState;
    private string? unsavedDependencyBoundary;
    private object? UnsavedGeneratedRecovery, unsavedDisabledControl;
    private int unsavedDisabledClickFrame;
    private bool unsavedDependencyBlocked;
    private bool unsavedProducerTabClicked;

    private bool ClickUnsavedDisabledWorkspace()
    {
        var button = Game.InspectRecoveryWorkspaceButton(unsavedReceipt!.SessionId);
        unsavedDisabledControl = button;
        if (button == null || !button.Visible || button.Frame < Gui.GetFrameCount() - 1) return false;
        Require(button.Session == unsavedReceipt.SessionId && button.Disabled && button.ItemId != 0,
            "Dependency-blocked native workspace button must retain its actual disabled item identity");
        var center = new System.Numerics.Vector2((button.MinimumX + button.MaximumX) / 2,
            (button.MinimumY + button.MaximumY) / 2);
        Require(button.MaximumX > button.MinimumX && button.MaximumY > button.MinimumY,
            "Disabled-click proof requires actual visible clipped button geometry");
        var io = Gui.GetIO();
        io.AddMouseViewportEvent(button.ViewportId); io.AddMousePosEvent(center.X, center.Y);
        if (System.Numerics.Vector2.DistanceSquared(io.MousePos, center) > 2) return false;
        io.AddMouseButtonEvent(0, true); return true;
    }

    private void StepUnsavedGeneratedScenario()
    {
        if (producer) ProduceUnsavedGenerated(); else ReceiveUnsavedGenerated();
    }
    private void AssertUnsavedGeneratedDisks()
    {
        foreach (var disk in disks)
            Require(File.Exists(disk.Path) && Hash(disk.Path) == disk.Sha256,
                "Unsaved recovery must preserve every original package/project/open-scene byte");
        if (unsavedProducer != null)
            Require(!File.Exists(unsavedProducer.Path) && !Directory.Exists(unsavedProducer.Path)
                && !File.Exists(unsavedProducer.GeneratedPath) && !Directory.Exists(unsavedProducer.GeneratedPath),
                "Unsaved producer/projection must remain absent from disk before and after explicit recovery consent");
    }
    private void ProduceUnsavedGenerated()
    {
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || !EnsureScenePresented()) break;
                Require(!Game.Document.IsDirty, "Unsaved producer arrangement must start from a clean original native scene");
                first = Game.Document; firstId = Game.VerificationDocumentId(first)!.Value;
                disks = Game.CaptureRecoveryUnsavedOriginalDisks();
                unsavedProducer = Game.PrepareRecoveryUnsavedGeneratedProducer(); Next(); break;
            case 3:
                unsavedProducerState = Game.InspectRecoveryUnsavedGeneratedProducer(unsavedProducer!, true);
                if (!unsavedProducerTabClicked)
                { unsavedProducerTabClicked = Click("document-tab/" + unsavedProducerState.GeneratedEntry); break; }
                if (!Game.NativeDocumentRestorationSettled
                    || !Game.VerifyRecoveryAcceptanceNativeFocus(unsavedProducer!.GeneratedPath, out restoredFocus)) break;
                var current = Game.CaptureRecoveryWorkspaceVerification();
                Require(current.Documents.Length == 3 && current.Groups.Groups.Count(group => group.Paths.Length != 0) == 1,
                    "New-producer fixture must contain exactly original scene, actual unsaved producer and generated view in one canonical group");
                if (++stable < 3) break;
                checkpoint = Game.BeginRecoveryWorkspaceVerificationCheckpoint(); Next(); break;
            case 4:
                if (!checkpoint!.IsCompleted) break;
                checkpoint.GetAwaiter().GetResult(); AssertUnsavedGeneratedDisks();
                var manifests = Directory.EnumerateFiles(journalRoot, "session.json", SearchOption.AllDirectories)
                    .Select(path => (Path: path, Manifest: JsonSerializer.Deserialize<EditorRecoveryManifest>(File.ReadAllBytes(path))))
                    .Where(value => value.Manifest?.ProcessId == Environment.ProcessId && value.Manifest.Workspace != null && !value.Manifest.Completed).ToArray();
                Require(manifests.Length == 1, "Genuine unsaved producer must have exactly one owned durable crash journal");
                var manifest = manifests[0].Manifest!;
                var payload = manifest.Documents.Single(entry => Same(entry.Path, unsavedProducer!.Path));
                Require(payload.AssetId == unsavedProducer!.AssetId && payload.Kind == "visual-script"
                    && payload.BaselineHash.Length == 0 && payload.PackagePath == unsavedProducer.ProducerDescriptor.PackagePath
                    && payload.AssetLocation != null && !manifest.Documents.Any(entry => Same(entry.Path, unsavedProducer.GeneratedPath)),
                    "Durable payload must be the actual native new asset with empty disk baseline; generated projection has no independent authored payload");
                Require(Hash(Path.Combine(Path.GetDirectoryName(manifests[0].Path)!, payload.PayloadHash + ".snapshot")) == payload.PayloadHash,
                    "New asset durable bytes must match the exact journal payload hash");
                unsavedReceipt = new(1, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    manifest.ProjectPath, manifests[0].Path, Hash(manifests[0].Path), manifest.SessionId,
                    manifest.Workspace!, unsavedProducer, payload.PayloadHash, disks);
                Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
                using (var stream = new FileStream(receiptPath + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, unsavedReceipt); stream.Flush(true); }
                File.Move(receiptPath + ".pending", receiptPath);
                durableReady = true; Next(); break;
            case 5: AssertUnsavedGeneratedDisks(); break; // Runner certifies real owned crash.
        }
    }
    private void ReceiveUnsavedGenerated()
    {
        switch (Phase)
        {
            case 0:
                Require(unsavedReceipt!.Version == 1 && Hash(unsavedReceipt.ManifestPath) == unsavedReceipt.ManifestSha256,
                    "New-producer receiver requires its actual intact durable crash manifest");
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (Game.Document == null || Game.VerificationDocumentViewport(Game.Document)?.RuntimeScene == null) break;
                first = Game.Document; firstId = Game.VerificationDocumentId(first)!.Value;
                AssertUnsavedGeneratedDisks();
                Require(Game.InspectRecoveryUnsavedDependencyFailure(unsavedReceipt!.Workspace)
                    == "Restore the visual-script asset before restoring its workspace.",
                    "Whole workspace recovery must explicitly block on the genuine missing new-producer payload consent");
                unsavedDependencyBoundary = Game.CaptureRecoveryUnsavedDependencyBoundary(unsavedProducer!);
                Next(); break;
            case 3:
                // The diagnostic native disabled button receipt is supplied by the
                // recovery panel; no managed Restore call substitutes for this click.
                if (!ClickUnsavedDisabledWorkspace()) break;
                unsavedDisabledClickFrame = Gui.GetFrameCount(); Next(); break;
            case 4: Gui.GetIO().AddMouseButtonEvent(0, false); Next(); break;
            case 5:
                if (Gui.GetFrameCount() <= unsavedDisabledClickFrame + 1) break;
                Require(Game.CaptureRecoveryUnsavedDependencyBoundary(unsavedProducer!) == unsavedDependencyBoundary
                    && !Game.IsRecoveryWorkspaceRestorePending && Game.RecoveryWorkspaceRestoreFailure == null,
                    "Actual disabled workspace click must not change canonical owners/history/groups/files or enqueue a transition");
                unsavedDependencyBlocked = true;
                if (ClickRecovery("recovery/document/" + unsavedReceipt!.SessionId + "/" + unsavedReceipt.ProducerPayloadHash, -1)) Next();
                break;
            case 6:
                if (Game.InspectRecoveryUnsavedDependencyFailure(unsavedReceipt!.Workspace) != null) break;
                unsavedConsentedState = Game.InspectRecoveryUnsavedGeneratedProducer(unsavedProducer!, false);
                AssertUnsavedGeneratedDisks(); Next(); break;
            case 7:
                if (ClickRecovery("recovery/workspace/" + unsavedReceipt!.SessionId, 1)) Next(); break;
            case 8:
                if (Game.IsRecoveryWorkspaceRestorePending) break;
                Require(Game.RecoveryWorkspaceRestoreFailure == null,
                    "Actual new-producer workspace Restore must finish without source membership/producer authority failures");
                unsavedRestoredState = Game.InspectRecoveryUnsavedGeneratedProducer(unsavedProducer!, true);
                Require(unsavedRestoredState.ProducerGeneration == unsavedConsentedState!.ProducerGeneration
                    && JsonSerializer.Serialize(unsavedRestoredState.History) == JsonSerializer.Serialize(unsavedConsentedState.History),
                    "Workspace-only restore must retain the explicitly consented native producer generation and history without duplicate creation/output operations");
                AssertUnsavedGeneratedDisks();
                if (ClickRecovery("recovery/close", -1)) Next(); break;
            case 9:
                if (!Game.NativeDocumentRestorationSettled
                    || !Game.VerifyRecoveryAcceptanceNativeFocus(unsavedProducer!.GeneratedPath, out restoredFocus)) break;
                var actual = Game.CaptureRecoveryWorkspaceVerification();
                Require(actual.Documents.Length == 3
                    && actual.Documents.OrderBy(document => document.Path).SequenceEqual(unsavedReceipt!.Workspace.Documents.OrderBy(document => document.Path))
                    && JsonSerializer.Serialize(actual.Groups) == JsonSerializer.Serialize(unsavedReceipt.Workspace.Groups)
                    && Same(actual.ActivePath!, unsavedReceipt.Workspace.ActivePath!),
                    "Actual canonical producer/generated/scene descriptors, native group membership and global active owner must match the durable workspace");
                nativeIniComparison = new { expected = unsavedReceipt.Workspace.NativeIni, actual = actual.NativeIni,
                    expectedDocking = DockingIni(unsavedReceipt.Workspace.NativeIni), actualDocking = DockingIni(actual.NativeIni),
                    liveRoot = Game.InspectRecoveryAcceptanceDockGeometry(actual.ActivePath!) };
                Require(DockingIni(actual.NativeIni) == DockingIni(unsavedReceipt.Workspace.NativeIni),
                    "Unsaved dependency recovery must retain full native dock topology and selected tabs");
                Require(first != null && ReferenceEquals(Game.FindRecoveryAcceptanceDocument(first.Path), first)
                    && Game.VerificationDocumentId(first) == firstId
                    && Game.VerificationDocumentViewport(first)?.RuntimeScene != null,
                    "Original hidden scene must retain its actual native world after generated-view recovery");
                AssertUnsavedGeneratedDisks();
                UnsavedGeneratedRecovery = new { ActualNativeCreation = true, DependencyBlockedWithoutMutation = unsavedDependencyBlocked,
                    ExplicitProducerConsent = true, ActualCompilerOwned = true, CurrentNativeProducerLease = unsavedRestoredState!.CurrentNativeProducerLease,
                    ProducerDiskRemainedAbsent = true, GeneratedDiskRemainedAbsent = true,
                    ProducerAssetId = unsavedProducer.AssetId, ProducerPath = unsavedProducer.Path, unsavedProducer.GeneratedPath,
                    CompiledSourceSha256 = unsavedProducer.CompilerSha256, RecoveredProducerPayloadHash = unsavedReceipt.ProducerPayloadHash,
                    unsavedConsentedState, unsavedRestoredState, restoredFocus, originalSceneWorldRetained = true };
                Finish(true, "Actual native new-producer payload consent enables workspace recovery; current generated lease/compiler/groups restored without implicit file writes");
                break;
        }
    }
}
