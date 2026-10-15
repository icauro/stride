using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal static class WindowsRepeatInputTiming
{
    internal static (double Delay, double Interval) Read()
    {
        if (!SystemParametersInfoW(0x0016, 0, out var delay, 0) || !SystemParametersInfoW(0x000A, 0, out var speed, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (delay > 3) delay = 0;
        if (speed > 31) speed = 31;
        return ((delay + 1) * .25, ((31 - speed) * (400 - 1000 / 30) / 31 + 1000 / 30) / 1000.0);
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, out uint value, uint flags);
}
