using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed partial class VisualCaptureEndpoint
{
    private static object SetBackgroundCaptureWindow(Window window, string state, double width, double height, double left, double top)
    {
        window.Dispatcher.VerifyAccess();
        if (!Environment.GetCommandLineArgs().Contains("--background-launch") || state != "normal" || window.WindowState != WindowState.Normal)
            throw new CaptureEndpointException("action_not_allowed", "Background geometry supports an already normal window and normal requested state only.");
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(left) || !double.IsFinite(top))
            throw new CaptureEndpointException("invalid_parameters", "Background window geometry must be finite DIP values.");
        var handle = new WindowInteropHelper(window).Handle;
        BackgroundGetWindowThreadProcessId(handle, out var pid);
        if (handle == IntPtr.Zero || pid != Environment.ProcessId || !BackgroundIsWindowVisible(handle)
            || BackgroundIsIconic(handle) || BackgroundIsZoomed(handle))
            throw new CaptureEndpointException("action_unavailable", "The owned normal visible native window is unavailable.");
        var scale = VisualTreeHelper.GetDpi(window);
        var nativeDpi = BackgroundGetDpiForWindow(handle);
        if (nativeDpi == 0 || Math.Abs(scale.DpiScaleX - nativeDpi / 96.0) > .01 || Math.Abs(scale.DpiScaleY - nativeDpi / 96.0) > .01)
            throw new CaptureEndpointException("geometry_unavailable", "Actual WPF and native window DPI are inconsistent.");
        int Pixel(double value, double factor) => checked((int)Math.Round(value * factor));
        var x = Pixel(left, scale.DpiScaleX); var y = Pixel(top, scale.DpiScaleY);
        var pixelsWide = Pixel(width, scale.DpiScaleX); var pixelsHigh = Pixel(height, scale.DpiScaleY);
        if (pixelsWide <= 0 || pixelsHigh <= 0) throw new CaptureEndpointException("invalid_parameters", "Requested native dimensions must be positive.");
        var previousContext = BackgroundSetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (!BackgroundGetWindowRect(handle, out var original)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var foregroundBefore = BackgroundGetForegroundWindow();
            // SWP_NOZORDER | SWP_NOACTIVATE. Never restore/maximize or change focus.
            if (!BackgroundSetWindowPos(handle, IntPtr.Zero, x, y, pixelsWide, pixelsHigh, 0x14))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Background native window sizing failed.");
            window.UpdateLayout();
            if (!BackgroundGetWindowRect(handle, out var actual) || BackgroundGetDpiForWindow(handle) != nativeDpi
                || actual.Left != x || actual.Top != y || actual.Right - actual.Left != pixelsWide || actual.Bottom - actual.Top != pixelsHigh)
            {
                if (!BackgroundSetWindowPos(handle, IntPtr.Zero, original.Left, original.Top, original.Right - original.Left,
                    original.Bottom - original.Top, 0x14)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to restore original native background bounds.");
                window.UpdateLayout();
                throw new CaptureEndpointException("geometry_mismatch", "Actual native bounds or DPI changed; original bounds were restored without activation.");
            }
            return new { state = "normal", requestedDip = new { width, height, left, top },
                actualPhysicalBounds = new { x = actual.Left, y = actual.Top, width = actual.Right - actual.Left, height = actual.Bottom - actual.Top },
                actualDip = new { width = window.ActualWidth, height = window.ActualHeight, left = window.Left, top = window.Top },
                dpi = nativeDpi, coordinateSpace = "screen-physical", flags = "SWP_NOZORDER|SWP_NOACTIVATE",
                foregroundBefore = CaptureNativeBounds.Handle(foregroundBefore), foregroundAfter = CaptureNativeBounds.Handle(BackgroundGetForegroundWindow()) };
        }
        finally { if (previousContext != IntPtr.Zero) BackgroundSetThreadDpiAwarenessContext(previousContext); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BackgroundWindowRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)] private static extern bool BackgroundSetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)] private static extern bool BackgroundGetWindowRect(IntPtr handle, out BackgroundWindowRect rectangle);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint BackgroundGetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")] private static extern uint BackgroundGetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")] private static extern IntPtr BackgroundSetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr BackgroundGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool BackgroundIsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "IsIconic")] private static extern bool BackgroundIsIconic(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "IsZoomed")] private static extern bool BackgroundIsZoomed(IntPtr handle);
}
