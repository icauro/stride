using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class OutputPanel
{
    // Original ExpressionDarkTheme TextLog brushes, packed in ImGui ABGR order.
    private static uint OutputMessageColor(LogMessageType level, EditorTheme theme) => (theme == EditorTheme.LightSteelBlue, level) switch
    {
        (true, LogMessageType.Debug) => 0xff4f4f2f,
        (true, LogMessageType.Verbose) => 0xff000000,
        (true, LogMessageType.Info) => 0xff006400,
        (true, LogMessageType.Warning) => 0xff008cff,
        (true, LogMessageType.Error) => 0xff0000ff,
        (true, LogMessageType.Fatal) => 0xff00008b,
        (false, LogMessageType.Debug) => 0xffa9a9a9,
        (false, LogMessageType.Verbose) => 0xffffffff,
        (false, LogMessageType.Info) => 0xff90ee90,
        (false, LogMessageType.Warning) => 0xff00d7ff,
        (false, LogMessageType.Error) => 0xff4763ff,
        (false, LogMessageType.Fatal) => 0xff0000ff,
        _ => Hexa.NET.ImGui.ImGui.GetColorU32(Hexa.NET.ImGui.ImGuiCol.Text),
    };
    private long observedMessageSequence;
    private void ObserveOutputMessages(EditorOutputEntry[] entries)
    {
        var first = 0; var end = entries.Length;
        while (first < end)
        {
            var middle = first + (end - first) / 2;
            if (entries[middle].Sequence <= observedMessageSequence) first = middle + 1;
            else end = middle;
        }
        var buildAdded = false;
        for (var index = first; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (!outputChannels.TryGetValue(entry.Channel, out var state)) continue;
            state.TextDirty = true;
            state.HasNewMessages = true;
            state.ErrorActivationSeen |= entry.Level is LogMessageType.Error or LogMessageType.Fatal;
            // The reference outer Output anchor activates on errors, then on
            // subsequent additions to that log until its collection is reset.
            if (state.ErrorActivationSeen) FocusRequested = true;
            if (entry.Channel != "Build") continue;
            buildAdded = true;
        }
        // Preserve the original ActivateOnLogBehavior latch until Clear/Reset.
        if (buildAdded && outputChannels["Build"].ErrorActivationSeen) requestedOutputChannel = "Build";
        if (entries.Length != 0) observedMessageSequence = Math.Max(observedMessageSequence, entries[^1].Sequence);
    }
}
