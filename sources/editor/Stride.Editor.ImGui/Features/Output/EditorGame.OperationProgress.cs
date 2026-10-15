using System.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorOperationProgress? operationProgress;
    private EditorOperationProgress OperationProgress => operationProgress ??= new();
    private string? operationProgressPopup;
    private Guid? operationProgressGeneration;
    private bool operationProgressOpened;

    private void SetOperationProgress(string ownerId, string title, string message, float? fraction = null, Action? cancel = null)
        => OperationProgress.Set(ownerId, title, message, fraction, cancel);
    private void AppendOperationProgressLog(string ownerId, string text) => operationProgress?.Append(ownerId, text);
    private void CompleteOperationProgress(string ownerId, Exception? error = null, bool cancelled = false)
        => operationProgress?.Complete(ownerId, error, cancelled);
    private void ClearOperationProgress(string ownerId) => operationProgress?.Clear(ownerId);
    private void ClearOperationProgress() => operationProgress?.Clear();

    private void DrawOperationProgress()
    {
        var current = operationProgress?.Next();
        if (operationProgressPopup != null && (current == null || operationProgressGeneration != current.Generation))
        {
            using var previous = ImGuiScopes.Modal(operationProgressPopup, ImGuiWindowFlags.None);
            if (previous.DrawContents) Gui.CloseCurrentPopup();
            operationProgressPopup = null; operationProgressGeneration = null; operationProgressOpened = false;
            return;
        }
        if (current == null) return;
        if (current.Running && Stopwatch.GetElapsedTime(current.Started).TotalMilliseconds < 500) return;
        operationProgressPopup ??= current.Title + "###OperationProgress/" + current.Generation;
        operationProgressGeneration = current.Generation;
        if (!operationProgressOpened) { Gui.OpenPopup(operationProgressPopup); operationProgressOpened = true; }
        Gui.SetNextWindowSize(UiScale(640, 480), ImGuiCond.Appearing);
        using var popup = ImGuiScopes.Modal(operationProgressPopup, ImGuiWindowFlags.None);
        if (!popup.DrawContents) return;
        var message = current.State switch
        {
            EditorOperationProgressState.Failed => "Operation failed.",
            EditorOperationProgressState.Cancelled => "Operation cancelled.",
            EditorOperationProgressState.Completed => "Operation completed.",
            _ => current.Message,
        };
        DrawSelectableText("##OperationMessage", EditorLocalization.Text(message));
        Gui.ProgressBar(current.Running ? current.Fraction ?? -(float)Gui.GetTime() : 1,
            new System.Numerics.Vector2(-1, UiScale(25)), "");
        var height = Math.Max(Gui.GetFrameHeight(), Gui.GetContentRegionAvail().Y - Gui.GetFrameHeightWithSpacing());
        if (Gui.BeginChild("##OperationLog", new System.Numerics.Vector2(0, height), ImGuiChildFlags.Borders))
            DrawSelectableText("##OperationLogText", current.Log);
        ImGuiScopes.EndChild();
        using (ImGuiScopes.Disabled(current.Running))
            if (DrawNativeButton(EditorLocalization.Widget("Close", "Button")))
            { OperationProgress.Clear(current.OwnerId); Gui.CloseCurrentPopup(); }
        if (current.Running && current.Cancel != null)
        {
            Gui.SameLine();
            if (DrawNativeButton(EditorLocalization.Widget("Cancel", "Button")))
                Execute(() => OperationProgress.RequestCancel(current.OwnerId, current.Generation));
        }
    }
}
