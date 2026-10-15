using System.Text.Json;
using Hexa.NET.ImGui;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string RecoveryCompensationControlledFailure = "Controlled recovery preparation failure after an actual added native scene view became ready.";
    private bool recoveryCompensationVerificationArmed, recoveryCompensationVerificationInjected;
    private EditorRecoveryWorkspaceState? recoveryCompensationVerificationBaseline;
    private (EditorDocumentEntry Entry, IEditorDocument Document, EditorAuthoringState? State, Guid? Generation, bool Dirty)[]? recoveryCompensationVerificationOwners;
    private EngineViewport? recoveryCompensationVerificationViewport;
    private Guid? recoveryCompensationVerificationEntryId, recoveryCompensationVerificationGeneration;
    private long recoveryCompensationOutputBaselineSequence;

    internal void ArmRecoveryWorkspaceCompensationVerification()
    {
        if (recoveryCompensationVerificationArmed || recoveryCompensationVerificationInjected || IsRecoveryWorkspaceRestorePending || workspace == null)
            throw new InvalidOperationException("Recovery compensation verification requires an idle loaded private fixture.");
        var arguments = Environment.GetCommandLineArgs();
        var scenario = Array.IndexOf(arguments, "--engine-capability");
        if (!arguments.Contains("--ui-test") || !arguments.Contains("--recovery-compensation-after-scene-open")
            || scenario < 0 || scenario + 1 >= arguments.Length || arguments[scenario + 1] != "E08.recovery.read"
            || !arguments.Contains("--workspace-recovery-fixture") || !arguments.Contains("--recovery-test-root"))
            throw new InvalidOperationException("The controlled preparation failure requires its explicit private recovery receiver selector.");
        // Resolve validates the actual owned Temp directory and its canonical receipt; never copy or accept a user project.
        if (!string.Equals(WorkspaceRecoveryFixtureGuard.Resolve(workspace.OpenPath, arguments), workspace.OpenPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recovery compensation verification has another private fixture owner.");
        recoveryCompensationVerificationBaseline = CaptureRecoveryWorkspace();
        recoveryCompensationVerificationOwners = documents.Entries.Select(entry =>
            (entry, entry.Document, entry.State, entry.State?.Generation, entry.Document.IsDirty)).ToArray();
        long logVersion = -1;
        outputLog.Read(ref logVersion, out var messages);
        recoveryCompensationOutputBaselineSequence = messages.LastOrDefault()?.Sequence ?? 0;
        recoveryCompensationVerificationArmed = true;
    }

    private void InjectRecoveryWorkspaceCompensationFailure(EditorDocumentEntry entry)
    {
        if (!recoveryCompensationVerificationArmed || entry.Document is not EntityHierarchyDocument hierarchy) return;
        if (!documents.Entries.Contains(entry) || entry.State == null || !ReferenceEquals(entry.State.Document, hierarchy)
            || recoveryCompensationVerificationOwners!.Any(owner => ReferenceEquals(owner.Entry, entry)))
            throw new InvalidOperationException("The controlled preparation failure has no actual added canonical scene generation.");
        var viewport = VerificationDocumentViewport(hierarchy);
        if (viewport?.RuntimeScene == null || !CanPresentSceneDocument(entry)) return; // Actual owner-frame preparation must finish first.
        recoveryCompensationVerificationViewport = viewport;
        recoveryCompensationVerificationEntryId = entry.Id;
        recoveryCompensationVerificationGeneration = entry.State.Generation;
        recoveryCompensationVerificationArmed = false;
        recoveryCompensationVerificationInjected = true;
        throw new InvalidOperationException(RecoveryCompensationControlledFailure);
    }

    internal object VerifyRecoveryWorkspaceCompensation()
    {
        if (!recoveryCompensationVerificationInjected || IsRecoveryWorkspaceRestorePending
            || recoveryWorkspaceRestoreFailure != RecoveryCompensationControlledFailure || recoveryCompensationVerificationViewport == null)
            throw new InvalidOperationException("The actual controlled recovery failure and compensation have not settled.");
        var owners = recoveryCompensationVerificationOwners!;
        if (documents.Entries.Count != owners.Length || owners.Any(owner => !documents.Entries.Contains(owner.Entry)
            || !ReferenceEquals(owner.Entry.Document, owner.Document) || !ReferenceEquals(owner.Entry.State, owner.State)
            || owner.Entry.State?.Generation != owner.Generation || owner.Document.IsDirty != owner.Dirty)
            || !recoveryCompensationVerificationViewport.IsResourceRetirementComplete)
            throw new InvalidOperationException("Recovery compensation did not preserve original native owners or retire the added view.");
        var actual = CaptureRecoveryWorkspace();
        var baseline = recoveryCompensationVerificationBaseline!;
        if (actual.ActivePath != baseline.ActivePath || JsonSerializer.Serialize(actual.Groups) != JsonSerializer.Serialize(baseline.Groups))
            throw new InvalidOperationException("Recovery compensation did not restore original document-group focus and membership.");
        return new { scenarioPassed = true, coverage = "Actual added native scene preparation failure followed by normal view retirement and retained-owner compensation; outer terminal remains required.",
            injectedEntry = recoveryCompensationVerificationEntryId, injectedGeneration = recoveryCompensationVerificationGeneration,
            actualNativeRetirementCompleted = true, retainedOwnerCount = owners.Length, originalFailure = recoveryWorkspaceRestoreFailure,
            restoredGroups = actual.Groups, restoredActivePath = actual.ActivePath };
    }
    internal object? InspectRecoveryWorkspaceCompensationVerification()
    {
        if (!args.Contains("--ui-test") || !args.Contains("--recovery-compensation-after-scene-open")) return null;
        var owners = recoveryCompensationVerificationOwners;
        var viewport = recoveryCompensationVerificationViewport;
        return new
        {
            armed = recoveryCompensationVerificationArmed, injected = recoveryCompensationVerificationInjected,
            pending = IsRecoveryWorkspaceRestorePending, originalFailure = recoveryWorkspaceRestoreFailure,
            expectedOwnerCount = owners?.Length, actualOwnerCount = documents.Entries.Count,
            retainedOwners = owners?.Select(owner => new
            {
                owner.Entry.Id, owner.Entry.Path, entryPresent = documents.Entries.Contains(owner.Entry),
                documentSame = ReferenceEquals(owner.Entry.Document, owner.Document),
                stateSame = ReferenceEquals(owner.Entry.State, owner.State), expectedGeneration = owner.Generation,
                actualGeneration = owner.Entry.State?.Generation, generationSame = owner.Entry.State?.Generation == owner.Generation,
                expectedDirty = owner.Dirty, actualDirty = owner.Document.IsDirty, dirtySame = owner.Document.IsDirty == owner.Dirty,
            }).ToArray(),
            actualEntries = documents.Entries.Select(entry => new { entry.Id, entry.Path, type = entry.Document.GetType().FullName,
                generation = entry.State?.Generation, entry.Document.IsDirty }).ToArray(),
            injectedEntry = recoveryCompensationVerificationEntryId, injectedGeneration = recoveryCompensationVerificationGeneration,
            injectedViewportPresent = viewport != null, injectedResourceRetirementComplete = viewport?.IsResourceRetirementComplete,
            injectedCpuDrainComplete = viewport?.IsSceneRetirementDrainComplete,
            retainedBorrowerPresent = viewport != null && retiredSceneDocumentBorrowers.Any(view => ReferenceEquals(view.Viewport, viewport)),
            cpuDrainingViewPresent = viewport != null && drainingSceneDocumentViews.Any(view => ReferenceEquals(view.Viewport, viewport)),
            gpuRetirementPending = IsEditorGpuRetirementPending, gpuRetirementFinalizing = IsEditorGpuRetirementFinalizing,
        };
    }
    internal unsafe (uint DockId, uint TabId, object Evidence)? InspectRecoveryCompensationOutputActivation()
    {
        if (!args.Contains("--ui-test") || !args.Contains("--recovery-compensation-after-scene-open")) return null;
        if (!recoveryCompensationVerificationInjected || recoveryWorkspaceRestoreFailure != RecoveryCompensationControlledFailure)
            throw new InvalidOperationException("Output activation proof requires the exact original controlled recovery failure.");
        long logVersion = -1;
        outputLog.Read(ref logVersion, out var messages);
        var error = messages.LastOrDefault(entry => entry.Sequence > recoveryCompensationOutputBaselineSequence
            && entry.Channel == "Project" && entry.Level == LogMessageType.Error && entry.Module == "recovery"
            && entry.MessageText == RecoveryCompensationControlledFailure);
        if (error == null) throw new InvalidOperationException("The actual controlled recovery cause is absent from the Project Error log.");
        var window = ImGuiP.FindWindowByName(EditorPanels.Output.DisplayWindowName);
        if (window.IsNull || !window.WasActive || window.Hidden || window.DockNode.IsNull || window.DockNode.TabBar.IsNull) return null;
        var bar = window.DockNode.TabBar;
        if (Gui.GetFrameCount() - window.LastFrameActive is not (0 or 1) || Gui.GetFrameCount() - bar.CurrFrameVisible is not (0 or 1)
            || bar.SelectedTabId != window.TabId || bar.VisibleTabId != window.TabId) return null;
        return (window.DockId, window.TabId, new { expectedOutputActivation = true, originalFailure = recoveryWorkspaceRestoreFailure,
            error.Sequence, error.Channel, severity = error.Level.ToString(), error.Module, error.MessageText,
            baselineLogSequence = recoveryCompensationOutputBaselineSequence, actualDockId = window.DockId,
            actualTabId = window.TabId, actualSelectedTabId = bar.SelectedTabId, actualVisibleTabId = bar.VisibleTabId,
            actualFrame = window.LastFrameActive, currentFrame = Gui.GetFrameCount() });
    }
}
