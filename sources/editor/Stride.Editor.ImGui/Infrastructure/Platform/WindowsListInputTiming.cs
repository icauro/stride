using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal static class WindowsListInputTiming
{
    internal static double SearchTimeoutSeconds => GetDoubleClickTime() * .002;
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
}
