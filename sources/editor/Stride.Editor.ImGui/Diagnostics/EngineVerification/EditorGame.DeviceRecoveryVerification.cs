using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool verificationDeviceRecoveryQueued;
    private bool verificationDeviceRecoveryStarted;
    internal bool VerificationDeviceRecoveryStarted => verificationDeviceRecoveryStarted;
    internal bool VerificationDeviceRecoveryPending => verificationDeviceRecoveryQueued || IsEditorGraphicsRecovering || IsGraphicsDeviceRecoveryPending;
    internal Exception? VerificationDeviceRecoveryError => GraphicsDeviceRecoveryFailure;
    internal object? VerificationWorkspaceOwner => workspace;
    internal object? VerificationHistoryOwner => sessionHistory;
    internal object? VerificationCompiledProjectOwner => projectContent;
    internal Guid[] VerificationHistoryTransactions => sessionHistory?.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray() ?? [];
    internal object? VerificationRecoveryHistoryEntries => sessionHistory?.History;

    internal void QueueVerificationDeviceRecovery()
    {
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Environment.GetCommandLineArgs().Contains("E06.recovery", StringComparer.OrdinalIgnoreCase)
            || document == null || !Path.GetFullPath(document.Path).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase)
            || Stride.Graphics.GraphicsDevice.Platform.ToString() != "Direct3D11")
            throw new InvalidOperationException("Device recovery verification requires the explicit E06.recovery private D3D11 fixture.");
        if (verificationDeviceRecoveryStarted || VerificationDeviceRecoveryPending || IsAuthoringPipelineUpdating)
            throw new InvalidOperationException("The controlled recovery must start once from an idle native owner.");
        verificationDeviceRecoveryQueued = true;
    }

    // Host calls this at the start of BeginDraw, before the recovery pump and
    // before base.BeginDraw starts a command frame. No global driver reset occurs.
    internal void PumpVerificationDeviceRecoveryBoundary()
    {
        if (!verificationDeviceRecoveryQueued) return;
        if (graphicsFrameOpen) throw new InvalidOperationException("Controlled recovery cannot interrupt an active ImGui frame.");
        verificationDeviceRecoveryQueued = false;
        var failure = GraphicsDeviceException.FromDeviceFailure(GraphicsDevice,
            "E06.recovery controlled local replacement; physical driver-loss detection is outside this case.", GraphicsDeviceStatus.Reset);
        if (!RequestEditorGraphicsRecovery(failure))
            throw new InvalidOperationException("The actual editor graphics recovery host rejected the controlled request.");
        verificationDeviceRecoveryStarted = true;
    }
}
