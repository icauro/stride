using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static partial class ImGuiScopes
{
    internal static void EndWindow([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { TraceLayoutBoundary("End", file, line); Gui.End(); }
    internal static void EndChild([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { TraceLayoutBoundary("EndChild", file, line); Gui.EndChild(); }

    internal static unsafe void TraceLayoutPhase(string phase)
    {
        if (Environment.GetEnvironmentVariable("STRIDE_EDITOR_TRACE_LAYOUT") != "1") return;
        var window = ImGuiP.GetCurrentWindowRead();
        if (window.IsNull) return;
        var name = Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
        Console.Error.WriteLine($"LAYOUT PHASE frame={Gui.GetFrameCount()} phase={phase} window={name} setPos={window.DC.IsSetPos} cursor={window.DC.CursorPos} registeredMax={window.DC.CursorMaxPos} available={Gui.GetContentRegionAvail()} skipItems={window.SkipItems}");
        Console.Error.Flush();
    }

    // Opt-in diagnostics preserve the native assertion and never submit items or
    // repair/change cursor/window state. stderr is synchronously written before native End.
    internal static unsafe void TraceLayoutBoundary(string boundary, string file = "", int line = 0)
    {
        if (Environment.GetEnvironmentVariable("STRIDE_EDITOR_TRACE_LAYOUT") != "1") return;
        var window = ImGuiP.GetCurrentWindowRead();
        if (window.IsNull || window.DC.IsSetPos == 0) return;
        var cursor = window.DC.CursorPos;
        var maximum = window.DC.CursorMaxPos;
        if (cursor.X <= maximum.X && cursor.Y <= maximum.Y) return;
        var name = Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
        Console.Error.WriteLine($"LAYOUT BOUNDARY {boundary}: window={name}, cursor={cursor}, registeredMax={maximum}, skipItems={window.SkipItems}, source={file}:{line}\n{Environment.StackTrace}");
        Console.Error.Flush();
    }
}
