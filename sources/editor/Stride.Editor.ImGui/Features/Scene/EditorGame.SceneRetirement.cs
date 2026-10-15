namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void EnsureSceneCpuRetirementCompleted()
    {
        // Teardown never runs native callbacks after its protected GPU certificate.
        // Actual shutdown/pumping belongs to the explicit scene barrier's Initial phase.
        foreach (var view in LiveSceneDocumentViewports)
            if (!view.IsSceneRetirementDrainComplete)
                throw new InvalidOperationException("Scene workers and their owner-thread callbacks must drain before session/device teardown.");
        if (engineViewport != null && !engineViewport.IsSceneRetirementDrainComplete)
            throw new InvalidOperationException("The active native scene has not completed its protected CPU retirement.");
        foreach (var owner in retiredEditorPreviews)
            if (owner.Preview is IEditorResourceRetirementDrain drain && !drain.IsRetirementDrainComplete)
                throw new InvalidOperationException("A retained native scene still owns unfinished CPU work.");
    }
}
