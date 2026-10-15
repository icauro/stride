using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal static class WindowsDpi
{
    private static readonly nint PerMonitorAwareV2 = -4;

    // Call before any HWND is created. A manifest or hosting process may already
    // have fixed process awareness; in that case set awareness on the editor thread.
    public static void EnablePerMonitorAwareness()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!SetProcessDpiAwarenessContext(PerMonitorAwareV2))
                SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            try { SetProcessDpiAwareness(2); }
            catch (DllNotFoundException) { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
        }
    }

    public static float GetScale(nint window)
    {
        if (!OperatingSystem.IsWindows() || window == 0) return 1;
        try
        {
            var dpi = GetDpiForWindow(window);
            return dpi == 0 ? 1 : dpi / 96f;
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 8.1 supports per-monitor awareness but not GetDpiForWindow.
            var monitor = MonitorFromWindow(window, 2);
            if (monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0)
                return dpi / 96f;
            return 1;
        }
    }

    public static float GetScaleAtPosition(int x, int y)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, 2);
        try
        {
            return monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi != 0 ? dpi / 96f : 1;
        }
        catch (EntryPointNotFoundException) { return 1; }
        catch (DllNotFoundException) { return 1; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int awareness);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
