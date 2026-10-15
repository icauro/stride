namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    private readonly List<EditorPresenterTarget> failedUIRenderTargets = [];
    internal void RetainFailedPresenterTarget(EditorPresenterTarget target) => failedUIRenderTargets.Add(target);
    internal EditorPresenterTarget GuardUIRenderTarget(EditorPresenterTarget target)
    {
        var queued = false;
        return new(target.Presenter, () =>
        {
            try { target.Dispose(); }
            catch { if (!queued) { queued = true; RetainFailedPresenterTarget(target); } throw; }
        });
    }
    private void DrainUIRenderTargets()
    {
        foreach (var target in failedUIRenderTargets.ToArray()) { target.Dispose(); failedUIRenderTargets.Remove(target); }
    }
}
