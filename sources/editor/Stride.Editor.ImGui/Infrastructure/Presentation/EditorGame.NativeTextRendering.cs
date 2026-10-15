namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private WindowsGlyphImages? scriptGlyphImages;
    private string? reportedGlyphRetirementFailure;
    private WindowsGlyphImages GetScriptGlyphImages()
        => scriptGlyphImages ??= new(GraphicsDevice, renderer ?? throw new InvalidOperationException("The source renderer is not initialized."));
    private Task BeginScriptGlyphImageDrain()
    {
        SuspendNativeReadOnlyTextPresentation();
        foreach (var editor in scriptEditors.Values) editor.SuspendSourcePresentation();
        return scriptGlyphImages?.BeginDrain() ?? Task.CompletedTask;
    }
    // Normal frame: call after all main and detached renderer commands were submitted.
    private void EndScriptGlyphImageFrame()
    {
        scriptGlyphImages?.EndSubmittedFrame();
        ReportScriptGlyphRetirementFailure();
    }
    // Recovery/close: only true at the certified host submission/idle boundary.
    private void PumpScriptGlyphImages(bool submittedBoundary = false)
    {
        if (scriptGlyphImages == null) return;
        if (submittedBoundary)
        {
            if (!GraphicsDevice.IsPhysicallyUnavailable) GraphicsContext.CommandList.SubmitRecordedWorkForRetirement();
            scriptGlyphImages.SealSubmittedRetirements();
        }
        scriptGlyphImages.PumpRetirements();
        ReportScriptGlyphRetirementFailure();
    }
    private void ReportScriptGlyphRetirementFailure()
    {
        var failure = scriptGlyphImages?.RetirementFailure;
        if (failure == reportedGlyphRetirementFailure) return;
        reportedGlyphRetirementFailure = failure;
        if (failure != null)
            outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Native text rendering", failure);
    }
    private void ReleaseDrainedScriptGlyphImages()
    {
        scriptGlyphImages?.Dispose(); scriptGlyphImages = null;
        reportedGlyphRetirementFailure = null;
        foreach (var editor in scriptEditors.Values) { editor.NativeGlyphImages = null; editor.NativeGlyphCommands = null; }
    }
}
