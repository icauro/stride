namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Task componentImageRetirement = Task.CompletedTask;
    private string? reportedComponentImageRetirementFailure;
    private Task DrainEditorComponentImagesAsync(bool graphicsQuiesced = false)
        => componentImageRetirement = icons?.BeginComponentImageDrain(graphicsQuiesced) ?? Task.CompletedTask;

    private void BindEditorComponentImages()
    {
        if (icons == null) return;
        if (componentImageRetirement.IsCompleted && !IsEditorGpuRetirementPending && !RecoveryTransitionBusy && !IsEditorGraphicsRecovering
            && !editorExtensionsStopping && !closeRequested && !closePromptOpen && !exitApproved)
            icons.ResumeComponentImages();
        icons.BindComponentImages(sessionHistory?.Extensions);
    }

    private void CompleteComponentImageRender()
    {
        try
        {
            icons?.RetireUnusedComponentImages();
            var failure = icons?.ComponentImageRetirementFailure;
            if (failure != reportedComponentImageRetirementFailure)
            {
                reportedComponentImageRetirementFailure = failure;
                if (failure != null) outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Component image retirement", failure);
            }
        }
        catch (Stride.Graphics.GraphicsDeviceException) { throw; }
        catch (Exception error)
        {
            if (reportedComponentImageRetirementFailure == error.ToString()) return;
            reportedComponentImageRetirementFailure = error.ToString();
            RecordAssetPreviewRenderFailure("Component image retirement", error);
        }
    }
}
