namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private UIEditorViewport? recoveryReplacement;
    private void ReleaseRecoveryReplacement()
    {
        if (recoveryReplacement == null) return;
        recoveryReplacement.Dispose();
        recoveryReplacement = null;
    }
    public void ReleaseGraphicsResourcesForRecovery()
    {
        ReleaseRecoveryReplacement();
        CancelHierarchyExpansion();
        StopCameraInput();
        CancelCanvasPointer();
        StopLayoutDrag();
        document?.EndEdit();
        if (viewport == null) return;
        restoreView = viewport.CaptureCameraView();
        viewport.Dispose(); viewport = null;
    }
    public void RebindGraphicsResourcesAfterRecovery()
    {
        ReleaseRecoveryReplacement();
        if (document == null || viewport != null || !host.IsCurrentPresentationOwner(document)) return;
        var source = document;
        try
        {
            recoveryReplacement = host.CreateViewport(source);
            recoveryReplacement.Synchronize(source, displayedRoot);
            if (!ReferenceEquals(document, source) || !host.IsCurrentPresentationOwner(source))
                throw new InvalidOperationException("The UI presentation owner changed during graphics recovery.");
            var generation = host.ContentGeneration(source);
            viewport = recoveryReplacement;
            recoveryReplacement = null;
            contentGeneration = generation;
            viewportFailure = null; failedLayoutVersion = -1;
        }
        catch (Exception failure)
        {
            try { ReleaseRecoveryReplacement(); }
            catch (Exception cleanup) { throw new AggregateException("The UI viewport could not be restored or released.", failure, cleanup); }
            // A malformed asset remains available to the CPU editor. The existing
            // failure reporter rethrows actual device loss rather than isolating it.
            FailViewport(source, failure);
        }
    }
}
