using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class DiagnosticCompletion(bool passed, string result, Action<bool, string> completed)
    {
        internal bool Passed = passed, CancellationRequested, TransitionStarted, Finished;
        internal string Result = result;
        internal string? Failure;
        internal readonly Action<bool, string> Completed = completed;
    }
    private DiagnosticCompletion? diagnosticCompletion;
    internal bool DiagnosticCompletionActive => diagnosticCompletion is { Finished: false };
    internal string? DiagnosticCompletionFailure => diagnosticCompletion?.Failure;

    /// <summary>Freeze the scenario first. Report its final result only after the native close barrier completes.</summary>
    internal void BeginDiagnosticCompletion(bool passed, string result, Action<bool, string> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (!args.Contains("--ui-test") && !args.Contains("--engine-scene-test")
            && !args.Contains("--engine-picking-test") && !args.Contains("--engine-preview-test"))
            throw new InvalidOperationException("Diagnostic completion is restricted to explicit disposable test sessions.");
        if (diagnosticCompletion != null) return;
        var artifacts = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifacts))
            ConfigureDiagnosticCompletionTrace(Path.Combine(artifacts, "teardown-progress.jsonl"));
        diagnosticCompletion = new(passed, result, completed);
        CancelDocumentGroupParityLayoutRestore();
        ReleaseDiagnosticInput();
        WriteDiagnosticCompletionProgress(force: true);
    }

    // Invoke from the normal owning-thread Update. Suppress PumpEditorClosing while this actor is active.
    // Keep normal recovery/GPU retirement Update and submitted EndDraw pumping enabled.
    internal void PumpDiagnosticCompletion()
    {
        if (diagnosticCompletion != null) WriteDiagnosticCompletionProgress();
        if (diagnosticCompletion is not { Finished: false } state || state.Failure != null) return;
        try
        {
            ReleaseDiagnosticInput();
            if (exitApproved)
            {
                // Native-close tests may already have traversed the normal protected close.
                EnsureSceneCpuRetirementCompleted(); EnsureEditorGpuRetirementCompleted(); EnsureInstalledFontGraphicsRetired();
                state.Finished = true; WriteDiagnosticCompletionProgress(force: true); state.Completed(state.Passed, state.Result); return;
            }
            if (RecoveryTransitionBusy) return;
            if (!TryDrainCompilerStartRetirementParityVerification()) return;
            if (!TryDrainPackageDiskConflictParityVerification()) return;
            if (!NativeCompilerProcessRetirement.TryDrainRetirements()) return;
            if (!OwnedProjectBuildOutput.TryDrainFailedOutputs()) return;
            if (!TryDrainScriptAssistanceRetirements()) return;
            if (!TryDrainShellParityCleanup()) return;
            if (!TryDrainMixedSaveParityVerification()) return;
            if (!TryDrainRecoveryIntegrityParityVerification()) return;
            if (!TryDrainScriptSemanticAssistanceVerification()) return;
            if (!TryDrainScriptCompletionUiVerification()) return;
            if (!TryRetireAssetInheritanceParityVerification()) return;
            if (!TryRetireAssetCrossVolumeRelocationParityVerification()) return;
            if (!TryDrainActiveProjectPendingParityVerification()) return;
            if (!TryDrainActiveProjectStalePathParityVerification()) return;
            if (!TryDrainProjectFolderReadFailureParityVerification()) return;
            if (!TryDrainActiveProjectParityVerification()) return;
            if (!state.CancellationRequested)
            {
                ClearDocumentCloseRequests(); closePromptOpen = openClosePopup = false;
                RequestEditorClose();
                state.CancellationRequested = closeRequested;
                if (!state.CancellationRequested) return;
            }
            if (projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating) return;
            if (state.TransitionStarted) return;
            closeRequested = false;
            state.TransitionStarted = true;
            // Same teardown and async recovery/native GPU drain as normal ApproveEditorClose.
            // No Save/Discard is fabricated: retain the checkpoint for dirty disposable fixtures.
            BeginRecoveryProtectedClose(() =>
            {
                SaveSceneCameraSettings(); engineViewport?.ClearScene(); ClearDocumentSession();
            }, () =>
            {
                BeginTerminalCloseRetirement(() =>
                {
                    state.Finished = true;
                    WriteDiagnosticCompletionProgress(force: true);
                    state.Completed(state.Passed, state.Result);
                    Exit();
                }, error => FailDiagnosticCompletion(state, error));
            }, discardAuthorized: false, error => FailDiagnosticCompletion(state, error));
        }
        catch (Exception error) { FailDiagnosticCompletion(state, error); }
    }

    private void FailDiagnosticCompletion(DiagnosticCompletion state, Exception error)
    {
        state.Passed = false;
        state.Failure = "Test teardown failed: " + error;
        state.Result += Environment.NewLine + state.Failure;
        Console.Error.WriteLine(state.Failure);
        WriteDiagnosticCompletionProgress(force: true);
        // Retain actual owners and do not Exit into guarded teardown after a failed certificate.
        // The external bounded runner must report this failure and retain its fixture/artifacts.
    }
    private static void ReleaseDiagnosticInput()
    {
        var io = Gui.GetIO();
        for (var button = 0; button < 5; button++) io.AddMouseButtonEvent(button, false);
        foreach (var key in new[] { ImGuiKey.ModCtrl, ImGuiKey.ModAlt, ImGuiKey.ModShift, ImGuiKey.ModSuper,
                     ImGuiKey.A, ImGuiKey.Enter, ImGuiKey.Escape, ImGuiKey.Delete, ImGuiKey.F2 })
            io.AddKeyEvent(key, false);
    }
}
