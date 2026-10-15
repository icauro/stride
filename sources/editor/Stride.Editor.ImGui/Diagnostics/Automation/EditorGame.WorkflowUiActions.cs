namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Diagnostic hook immediately after an actual widget returns its action.
    internal void TraceWorkflowUiAction(string id, bool activated)
        => workflowTest?.ObserveUiAction(id, activated);
}
