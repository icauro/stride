using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool graphicsFailureClosePrompted;
    private bool IsGraphicsRecoveryCpuClosing;
    // CPU-only close replays actual discard operations while presentation is suspended.
    // A failed save/discard keeps the durable checkpoint and terminal native failure.
    private bool PumpGraphicsRecoveryClose(GraphicsRecoveryState state)
    {
        if (exitApproved || RecoveryTransitionBusy) return false;
        var terminalFailure = GraphicsDeviceRecoveryFailure;
        if (!closeRequested && (terminalFailure == null || graphicsFailureClosePrompted)) return true;
        if (IsEditorExtensionCommandRunning || IsPackageDependencyBusy || IsPackageMetadataSaving || IsUserProjectBuilding
            || IsEditorGpuRetirementPending || IsExternalAssetPreviewDrainPending || IsExternalAssetPreviewUpdating
            || IsAddingSolutionProjects || IsPackagePlatformUpdating || IsProjectCreationUpdating || IsActiveProjectSwitching || IsAssetImportUpdating
            || Volatile.Read(ref graphicsRecoveryDetachedBuilds) != 0
            || Volatile.Read(ref detachedFontBuilds) != 0 || Volatile.Read(ref detachedCompositorBuilds) != 0
            || Volatile.Read(ref detachedAssetImports) != 0) return false;
        closeRequested = false; closePromptOpen = openClosePopup = false;
        graphicsFailureClosePrompted = true;
        try
        {
            ValidatePendingPropertyStrings();
            FinishEntityRename(true); documents.CommitAll();
            var answer = WindowsGraphicsRecoveryDialog.ConfirmClose(Window.NativeWindow.Handle, terminalFailure?.Message);
            if (answer == 2) return false; // IDCANCEL: retain the canonical session and failure.
            if (answer != 6 && answer != 7) return false;
            if (answer == 6)
            {
                documents.SaveAll();
                if (workspace != null)
                {
                    RequestPackageMetadataSave(BeginClose, ReportSaveFailure);
                    return false;
                }
            }
            BeginClose();
            void BeginClose() => BeginRecoveryProtectedClose(() =>
            {
                // Drain actual GPU owners before graph/history leases. This is phase-idempotent
                // even if native replacement failed after the original device was released.
                if (state.ReleasePosition == 5 && (state.RebindPosition != 0 || renderer != null || engineViewport != null || platformViewports != null))
                {
                    // A failed rebind can already own resources on the replacement device.
                    // Restart the real release phases for those newly created owners.
                    state.ReleasePosition = 0;
                }
                ReleaseEditorGraphicsForRecovery();
                IsGraphicsRecoveryCpuClosing = true;
                try
                {
                    if (answer == 7) RevertWorkspaceMetadata();
                    ClearDocumentSession();
                }
                finally { IsGraphicsRecoveryCpuClosing = false; }
            }, () =>
            {
                exitApproved = true; closePromptOpen = false; CloseApproved?.Invoke(); Exit();
            }, answer == 7, error =>
            {
                statusMessage = error.Message;
                outputLog.Append("Graphics", LogMessageType.Error, "Close", error.Message, error.ToString());
                WindowsGraphicsRecoveryDialog.ReportFailure(Window.NativeWindow.Handle, error);
            });
            void ReportSaveFailure(Exception error)
            {
                statusMessage = error.Message;
                outputLog.Append("Graphics", LogMessageType.Error, "Save", error.Message, error.ToString());
                WindowsGraphicsRecoveryDialog.ReportFailure(Window.NativeWindow.Handle, error);
            }
        }
        catch (Exception error)
        {
            statusMessage = error.Message;
            outputLog.Append("Graphics", LogMessageType.Error, "Save", error.Message, error.ToString());
            WindowsGraphicsRecoveryDialog.ReportFailure(Window.NativeWindow.Handle, error);
        }
        return false;
    }
}
