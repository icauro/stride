using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Numerics;

namespace Stride.Editor.ImGui;

/// <summary>Owns one native relative-mouse gesture, restoring the prior Win32 registrations.</summary>
internal sealed unsafe class WindowsRelativeMouseCapture : IDisposable
{
    private const uint RawInput = 0xFF, ReadInput = 0x10000003, InputSink = 0x100;
    private const nuint SubclassId = 0x494D4341;
    private readonly Procedure procedure;
    private readonly Action<string> report;
    private RawDevice? previousMouse;
    private Rectangle previousClip, ownedClip;
    private bool subscribed, registered, clipped, stopped, destroying;
    private long dx, dy;
    internal nint Window { get; }
    internal Vector2 Anchor { get; }
    internal Vector2 TotalMotion { get; private set; }
    internal bool Active => !stopped && subscribed;
    internal WindowsRelativeMouseCapture(nint window, Action<string> report)
    {
        Window = window; this.report = report; procedure = OnMessage;
        if (!GetCursorPos(out var point) || !GetClipCursor(out previousClip)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Anchor = new(point.X, point.Y); ownedClip = new Rectangle { Left = point.X, Top = point.Y, Right = point.X + 1, Bottom = point.Y + 1 };
    }
    internal void Start()
    {
        previousMouse = MouseRegistration();
        if (!SetWindowSubclass(Window, procedure, SubclassId, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        subscribed = true;
        var device = new RawDevice { Page = 1, Usage = 2, Flags = InputSink, Target = Window };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        registered = true;
        if (!ClipCursor(ref ownedClip)) throw new Win32Exception(Marshal.GetLastWin32Error());
        clipped = true; SetCapture(Window); SetCursor(0);
    }
    internal Vector2 ConsumeMotion()
    {
        var result = new Vector2(Interlocked.Exchange(ref dx, 0), Interlocked.Exchange(ref dy, 0));
        TotalMotion += result; return result;
    }
    internal void KeepHidden() { if (Active) SetCursor(0); }
    private nint OnMessage(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        try
        {
            if (message == RawInput && Active)
            {
                uint bytes = 0; var headerSize = (uint)Marshal.SizeOf<RawHeader>();
                if (GetRawInputData(lparam, ReadInput, null, ref bytes, headerSize) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (bytes >= headerSize + 24 && bytes <= 4096)
                {
                    var buffer = stackalloc byte[(int)bytes];
                    var read = GetRawInputData(lparam, ReadInput, buffer, ref bytes, headerSize);
                    if (read == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (read >= headerSize + 24)
                    {
                        var header = (RawHeader*)buffer; var mouse = (RawMouse*)(buffer + headerSize);
                        // Absolute tablets/touch never become fabricated relative motion.
                        if (header->Type == 0 && (mouse->Flags & 1) == 0)
                        { Interlocked.Add(ref dx, mouse->X); Interlocked.Add(ref dy, mouse->Y); }
                    }
                }
            }
            if (message == 0x20 && Active) { SetCursor(0); return 1; }
            if (message == 0x82) destroying = true;
            if (message is 0x1F or 0x82 or 0x7E || message == 0x8 && (nint)wparam != Window
                || message == 0x215 && lparam != Window) Dispose();
        }
        catch (Exception error) { report("Camera pointer input: " + error.Message); }
        return DefSubclassProc(hwnd, message, wparam, lparam);
    }
    public void Dispose()
    {
        if (stopped) return;
        var failures = new List<Exception>();
        // Cursor confinement must not survive a failed registration restore.
        // Release independent native owners and retain only failed steps to retry.
        if (clipped)
            try
            {
                if (!GetClipCursor(out var current)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (current.Left == ownedClip.Left && current.Top == ownedClip.Top && current.Right == ownedClip.Right && current.Bottom == ownedClip.Bottom
                    && !ClipCursor(ref previousClip)) throw new Win32Exception(Marshal.GetLastWin32Error());
                clipped = false;
            }
            catch (Exception error) { failures.Add(error); }
        if (registered)
            try
            {
                var current = MouseRegistration();
                if (current is { Target: var target, Flags: InputSink } && target == Window)
                {
                    var restore = previousMouse is { } previous && (previous.Target == 0 || IsWindow(previous.Target) && !(destroying && previous.Target == Window))
                        ? previous : new RawDevice { Page = 1, Usage = 2, Flags = 1 };
                    if (!RegisterRawInputDevices([restore], 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                registered = false;
            }
            catch (Exception error) { failures.Add(error); }
        if (subscribed)
            try
            {
                if (IsWindow(Window) && !RemoveWindowSubclass(Window, procedure, SubclassId)) throw new Win32Exception(Marshal.GetLastWin32Error());
                subscribed = false;
            }
            catch (Exception error) { failures.Add(error); }
        if (GetCapture() == Window) ReleaseCapture();
        if (GetForegroundWindow() == Window && !destroying) SetCursorPos((int)Anchor.X, (int)Anchor.Y);
        SetCursor(LoadCursorW(0, 32512)); GC.KeepAlive(procedure);
        if (failures.Count != 0) throw new AggregateException("Native camera pointer restoration failed.", failures);
        stopped = true;
    }
    private static RawDevice? MouseRegistration()
    {
        uint count = 0; var size = (uint)Marshal.SizeOf<RawDevice>();
        if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (count == 0) return null;
        var values = new RawDevice[count];
        if (GetRegisteredRawInputDevices(values, ref count, size) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        foreach (var value in values) if (value.Page == 1 && value.Usage == 2) return value;
        return null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public nint Device; public nuint Parameter; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct RawMouse { [FieldOffset(0)] public ushort Flags; [FieldOffset(12)] public int X; [FieldOffset(16)] public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint Procedure(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, Procedure callback, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, Procedure callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRegisteredRawInputDevices([Out] RawDevice[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint input, uint command, void* data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClipCursor(out Rectangle rectangle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClipCursor(ref Rectangle rectangle);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadCursorW(nint module, nint resource);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
}
