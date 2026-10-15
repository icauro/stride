using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal bool WorkflowProjectPublicationPending => IsProjectLoading || RecoveryTransitionBusy;
    internal bool WorkflowScriptReloadPromptPending => sourceReloadPromptOpen;

    internal object InspectWorkflowInteractionGates() => new
    {
        IsAuthoringPipelineUpdating, RecoveryTransitionBusy, IsEditorGpuRetirementPending,
        IsEditorGpuRetirementFinalizing, IsEditorGraphicsRecovering, IsExternalAssetPreviewUpdating,
        IsExternalAssetPreviewDrainPending, IsCatalogPreviewUpdating, IsFontSettingsUpdating,
        IsTextureSettingsUpdating, IsMediaPreviewUpdating, IsCompositorSettingsUpdating,
        IsPackageDependencyBusy, HasPendingSpriteSourceEdits, closeRequested, closePromptOpen,
        closingDocument = closingDocument?.Id,
    };

    internal EditorOutputEntry[] CaptureWorkflowRecentOutput(int count = 128)
    {
        long version = -1;
        outputLog.Read(ref version, out var entries);
        return entries.TakeLast(Math.Clamp(count, 1, 512)).ToArray();
    }

    internal EditorOutputEntry? CaptureWorkflowCommandFailureAfter(long sequence)
        // These are actual load-command/adoption/recovery owners. Thumbnail and
        // unrelated preview failures remain in the bundle, but cannot establish
        // failure of a project load that has not started (e.g. a save prompt).
        => CaptureWorkflowRecentOutput().LastOrDefault(entry => entry.Sequence > sequence
            && (entry.Module is "Editor" or "Scene" or "Project loading cleanup"
                || string.Equals(entry.Module, "recovery", StringComparison.OrdinalIgnoreCase))
            && entry.Level is LogMessageType.Error or LogMessageType.Fatal);
}
