using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsScriptTextDragging => WindowsSourceTextDragDrop.IsDragging;
    private bool IsScriptOleFeedbackTick;
    private bool CanTransferScriptText => workspace != null && projectLoading == null && !RecoveryTransitionBusy
        && !IsAuthoringPipelineUpdating && !closeRequested && !closePromptOpen && closingDocument == null;
    protected override void OnCompletedTick()
    {
        base.OnCompletedTick();
        if (smokeTest || guiContext.IsNull || IsScriptOleFeedbackTick || IsScriptTextDragging || !CanTransferScriptText || IsEditorGpuRetirementFinalizing) return;
        Gui.SetCurrentContext(guiContext); PumpScriptTextDragDrop();
    }
    // Owner-thread only, after the whole native frame: OLE runs its own modal message loop.
    private void PumpScriptTextDragDrop()
    {
        WindowsSourceTextDragDrop.Frame = Gui.GetFrameCount();
        Execute(WindowsSourceTextDragDrop.ObserveFailure);
        if (!CanTransferScriptText) return;
        foreach (var editor in scriptEditors.Values.ToArray()) Execute(editor.PumpNativeSourceDrag);
    }
    private unsafe void TickScriptOleFeedback()
    {
        if (IsTickWorkActive || IsScriptOleFeedbackTick || !IsScriptTextDragging || !CanTransferScriptText || IsEditorGpuRetirementFinalizing || guiContext.IsNull) return;
        Gui.SetCurrentContext(guiContext);
        var previous = guiContext.Handle; var cursorPolicy = Gui.GetIO().ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange;
        IsScriptOleFeedbackTick = true; Gui.GetIO().ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
        try { Tick(); }
        finally
        {
            IsScriptOleFeedbackTick = false;
            if (!guiContext.IsNull && guiContext.Handle == previous)
            {
                Gui.SetCurrentContext(guiContext);
                if (cursorPolicy == 0) Gui.GetIO().ConfigFlags &= ~ImGuiConfigFlags.NoMouseCursorChange;
            }
        }
    }
    private unsafe void PrepareScriptOleFeedbackFrame()
    {
        if (!IsScriptOleFeedbackTick) return;
        guiInput.SuspendNativeModalInput(); platformViewports?.SuspendNativeModalInput();
        var io = Gui.GetIO(); io.ClearInputKeys(); io.ClearInputMouse(); io.InputQueueCharacters.Clear();
        // Native OLE owns these events; they must not become editor command/button input.
        Gui.GetCurrentContext().InputEventsQueue.Clear();
    }
}
