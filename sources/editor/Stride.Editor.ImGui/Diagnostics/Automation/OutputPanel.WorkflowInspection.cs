namespace Stride.Editor.ImGui;

internal sealed partial class OutputPanel
{
    internal Action<object>? WorkflowInteractionRecorder { get; set; }
    internal Action<string>? WorkflowControlRecorder { get; set; }
    internal OutputWorkflowInspection CaptureWorkflowState() => new(outputChannel, outputInfo,
        outputText, outputSearch, outputMatches.Count, outputMatchIndex, outputTextDirty || outputMatchesDirty);
}

internal sealed record OutputWorkflowInspection(string Channel, bool InfoEnabled, string VisibleText,
    string Search, int MatchCount, int MatchIndex, bool Updating);

internal sealed partial class EditorGame
{
    internal OutputWorkflowInspection InspectOutputWorkflow() => outputPanel.CaptureWorkflowState();
}
