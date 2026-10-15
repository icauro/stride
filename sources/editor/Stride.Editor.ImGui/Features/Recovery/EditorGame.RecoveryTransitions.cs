namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorRecoveryTransitionCoordinator? recoveryTransitions;
    private EditorRecoveryTransitionCoordinator RecoveryTransitions => recoveryTransitions ??= new();
    private bool RecoveryTransitionBusy => recoveryTransitions?.Busy == true;
    private TaskCompletionSource<bool>? recoveryWorkspaceDrainCompletion;
    private Task BeginRecoveryWorkspaceRestoreDrainAsync()
    {
        CancelRecoveryWorkspaceRestore();
        if (TryDrainRecoveryWorkspaceRestore()) return Task.CompletedTask;
        recoveryWorkspaceDrainCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        return recoveryWorkspaceDrainCompletion.Task;
    }
    private void PumpRecoveryWorkspaceDrainCompletion()
    {
        if (recoveryWorkspaceDrainCompletion == null || !TryDrainRecoveryWorkspaceRestore()) return;
        var completion = recoveryWorkspaceDrainCompletion;
        recoveryWorkspaceDrainCompletion = null;
        completion.TrySetResult(true);
    }
    private void RunAfterRecoveryWorkspaceDrain(Func<Task> operation, Action next, Action<Exception> failed)
    {
        RecoveryTransitions.Run(BeginRecoveryWorkspaceRestoreDrainAsync,
            () => RecoveryTransitions.Run(operation, next, failed), failed);
    }
    private void PumpRecoveryTransitions() => recoveryTransitions?.Pump();
    private Task FlushRecoveryAndPreviewDrainAsync(EditorRecoveryPanel? checkpoint)
    {
        var platformDrain = BeginPackagePlatformDrainAsync();
        return Task.WhenAll(checkpoint?.FlushTransitionAsync() ?? Task.CompletedTask,
            BeginEditorGpuRetirementAsync(retireScenes: true, retireFonts: true), platformDrain);
    }
    private void BeginWorkspaceRecoveryReplacement(Action action, bool discardAuthorized, Action? failed = null)
    {
        RecoveryTransitions.AuthorizeReplacement(ownedRecovery, discardAuthorized);
        // Authorize/checkpoint preparation without retiring the current workspace.
        // Project loading can still cancel or fail; successful adoption and an
        // actual project close each own their later platform/GPU retirement barrier.
        RunAfterRecoveryWorkspaceDrain(() => ownedRecovery?.FlushTransitionAsync() ?? Task.CompletedTask, action, error =>
        {
            RecoveryTransitions.AbortReplacement(); statusMessage = "Recovery checkpoint failed: " + error.Message;
            RecordAssetPreviewRenderFailure("recovery", error);
            failed?.Invoke();
        });
    }
    private void BeginRecoveryProjectAdoption(Action adoption, Action failed)
    {
        var previous = ownedRecovery;
        var mayComplete = RecoveryTransitions.DiscardAuthorized
            || workspace == null || !workspace.HasPackageChanges && !documents.AuthoredDocuments.Any(document => document.IsDirty);
        RunAfterRecoveryWorkspaceDrain(() => FlushRecoveryAndPreviewDrainAsync(previous), () =>
        {
            // After this point the old host may refer to new project state; never invoke its serializer again.
            ownedRecovery = null;
            try
            {
                adoption(); InitializeRecovery();
                RecoveryTransitions.AbortReplacement();
                if (previous != null && mayComplete)
                    RecoveryTransitions.Run(previous.CompleteCheckpointAsync, () => { }, ReportRecoveryTransitionFailure);
            }
            catch { RecoveryTransitions.AbortReplacement(); throw; }
        }, error => { RecoveryTransitions.AbortReplacement(); failed(); ReportRecoveryTransitionFailure(error); });
    }
    private void BeginRecoveryProtectedClose(Action teardown, Action finish, bool discardAuthorized, Action<Exception> failed)
    {
        var previous = ownedRecovery;
        var mayComplete = discardAuthorized || workspace == null || !workspace.HasPackageChanges && !documents.AuthoredDocuments.Any(document => document.IsDirty);
        RunAfterRecoveryWorkspaceDrain(() => FlushRecoveryAndPreviewDrainAsync(previous), () =>
        {
            ownedRecovery = null;
            // A failed teardown can already have released part of the old authoring graph.
            // Keep its last durable snapshot detached instead of replacing it with a partial/empty live capture.
            teardown();
            RecoveryTransitions.AbortReplacement();
            if (previous != null && mayComplete) RecoveryTransitions.Run(previous.CompleteCheckpointAsync, finish, failed);
            else finish();
        }, failed);
    }
    private void AbortRecoveryProjectReplacement() => recoveryTransitions?.AbortReplacement();
    private void ReportRecoveryTransitionFailure(Exception error)
    {
        statusMessage = "Recovery transition failed: " + error.Message; RecordAssetPreviewRenderFailure("recovery", error);
    }
}
