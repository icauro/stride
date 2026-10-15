using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    internal Action<string, bool>? AssistanceControlRecorder { get; set; }
    internal object CaptureAssistanceUiEvidence() => new
    {
        sourcePath, version, InputActive, CaretPosition, AssistanceGeneration, sourceSurfaceId,
        completionVersion = completion?.Source.Version, completionEpoch = completion?.Epoch,
        completionItems = completion?.Items.Select(item => item.DisplayText).ToArray(),
        nativeFrame = Gui.GetFrameCount(), activeId = Gui.GetCurrentContext().ActiveId,
        pendingRequest = assistRequest?.Kind.ToString()
    };
}
