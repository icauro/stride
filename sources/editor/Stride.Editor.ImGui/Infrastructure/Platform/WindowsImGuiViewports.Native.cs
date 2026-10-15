using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsImGuiViewports
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool MonitorProc(nint monitor, nint dc, ref Rect bounds, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Hwnd; public uint Id; public nuint Wparam; public nint Lparam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    { public uint Size, Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public uint Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionForm { public uint Style; public Point Position; public Rect Area; }
    [StructLayout(LayoutKind.Sequential)] private struct CandidateForm { public uint Index, Style; public Point Position; public Rect Area; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint instance, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AdjustWindowRectEx(ref Rect bounds, uint style, bool menu, uint extended);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AdjustWindowRectExForDpi(ref Rect bounds, uint style, bool menu, uint extended, uint dpi);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect bounds);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint hwnd, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out Message message, nint hwnd, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int kind, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint hwnd);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint hwnd, nint context);
    [DllImport("imm32.dll")] private static extern bool ImmSetCompositionWindow(nint context, ref CompositionForm value);
    [DllImport("imm32.dll")] private static extern bool ImmSetCandidateWindow(nint context, ref CandidateForm value);
    [DllImport("imm32.dll")] private static extern bool ImmNotifyIME(nint context, uint action, uint index, uint value);

    private static void AdjustClientRect(ref Rect rect, uint style, uint extended, float scale)
    {
        try { if (AdjustWindowRectExForDpi(ref rect, style, false, extended, (uint)Math.Round(96 * scale))) return; }
        catch (EntryPointNotFoundException) { }
        if (!AdjustWindowRectEx(ref rect, style, false, extended))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
