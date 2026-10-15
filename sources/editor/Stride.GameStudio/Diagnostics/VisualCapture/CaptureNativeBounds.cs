using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal static class CaptureNativeBounds
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out NativeRect rect, int size);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flag);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    internal static string Handle(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("X");
    internal static IntPtr ParseHandle(string hwnd) => new(Convert.ToInt64(hwnd[2..], 16));
    internal static bool IsVisible(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(GetAncestor(hwnd, 2));
    internal static bool Activate(IntPtr hwnd) => hwnd != IntPtr.Zero && SetForegroundWindow(GetAncestor(hwnd, 2));
    internal static bool IsForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var foreground = GetForegroundWindow(); var root = GetAncestor(hwnd, 2);
        return foreground == hwnd || foreground == root || GetAncestor(foreground, 3) == GetAncestor(hwnd, 3);
    }
    internal static double Dpi(IntPtr hwnd) { var dpi = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd); return dpi == 0 ? 96 : dpi; }
    internal static Rect WindowRect(IntPtr hwnd, bool client)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (client)
            {
                if (!GetClientRect(hwnd, out var rectangle)) return Rect.Empty;
                var point = new NativePoint(); if (!ClientToScreen(hwnd, ref point)) return Rect.Empty;
                return new Rect(point.X, point.Y, Math.Max(0, rectangle.Right - rectangle.Left), Math.Max(0, rectangle.Bottom - rectangle.Top));
            }
            if (GetAncestor(hwnd, 2) == hwnd && DwmGetWindowAttribute(hwnd, 9, out var composition, Marshal.SizeOf<NativeRect>()) == 0)
                return new Rect(composition.Left, composition.Top, Math.Max(0, composition.Right - composition.Left), Math.Max(0, composition.Bottom - composition.Top));
            return GetWindowRect(hwnd, out var bounds) ? new Rect(bounds.Left, bounds.Top, Math.Max(0, bounds.Right - bounds.Left), Math.Max(0, bounds.Bottom - bounds.Top)) : Rect.Empty;
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    internal static CaptureRectangle Rectangle(Rect rect) => rect.IsEmpty ? new(0, 0, 0, 0)
        : new(checked((int)Math.Floor(rect.Left)), checked((int)Math.Floor(rect.Top)), checked((int)Math.Max(0, Math.Ceiling(rect.Right) - Math.Floor(rect.Left))), checked((int)Math.Max(0, Math.Ceiling(rect.Bottom) - Math.Floor(rect.Top))));
    internal static string ClassName(IntPtr hwnd) { var text = new StringBuilder(512); GetClassName(hwnd, text, text.Capacity); return text.ToString(); }
    internal static string Title(IntPtr hwnd) { var text = new StringBuilder(2048); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
    internal static IReadOnlyList<IntPtr> ProcessWindows()
    {
        var handles = new HashSet<IntPtr>();
        EnumProc top = (hwnd, parameter) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != Environment.ProcessId) return true;
            handles.Add(hwnd);
            EnumProc child = (nested, childParameter) => { GetWindowThreadProcessId(nested, out var childPid); if (childPid == Environment.ProcessId) handles.Add(nested); return true; };
            EnumChildWindows(hwnd, child, IntPtr.Zero); GC.KeepAlive(child); return true;
        };
        EnumWindows(top, IntPtr.Zero); GC.KeepAlive(top); return handles.ToArray();
    }
}
