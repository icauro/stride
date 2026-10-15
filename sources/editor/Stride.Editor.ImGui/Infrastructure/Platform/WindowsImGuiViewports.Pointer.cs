using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsImGuiViewports
{
    private WindowsWindowMessages? mainInputMessages;
    private readonly HashSet<nint> trackingMouse = [];
    private readonly bool[] blockedMouse = new bool[5];
    private uint? primaryPointer;
    private nint pointerWindow;
    private void ObserveMainInput(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        var previous = Gui.GetCurrentContext(); Gui.SetCurrentContext(context);
        try
        {
            HandlePointerInput(hwnd, message, wparam, lparam);
            if (message == 0x2E0 || message == 0x7E) monitorsDirty = true;
        }
        finally { Gui.SetCurrentContext(previous); }
    }
    private bool HandlePointerInput(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        if (syntheticInput) return false;
        var io = Gui.GetIO();
        switch (message)
        {
            case 0x240: // WM_TOUCH: SDL may register this legacy transport on its main HWND.
                var count = (uint)(wparam & 0xFFFF);
                if (count == 0 || count > 256) return false;
                TouchInfo* contacts = stackalloc TouchInfo[(int)count];
                if (!GetTouchInputInfo(lparam, count, contacts, Marshal.SizeOf<TouchInfo>())) return false;
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        var touch = contacts[index];
                        if ((touch.Flags & 0x10) == 0 || (touch.Flags & 0x80) != 0) continue;
                        io.AddMouseSourceEvent((touch.Flags & 0x40) != 0 ? ImGuiMouseSource.Pen : ImGuiMouseSource.TouchScreen);
                        io.AddMousePosEvent(touch.X / 100f, touch.Y / 100f);
                        io.AddMouseViewportEvent(windowIds.GetValueOrDefault(hwnd));
                        var down = (touch.Flags & 4) == 0;
                        if (down) { primaryPointer = touch.Id; pointerWindow = hwnd; blockedMouse[0] = false; }
                        MouseButton(io, 0, down);
                        if (!down && primaryPointer == touch.Id)
                        { primaryPointer = null; pointerWindow = 0; blockedMouse[0] = true; }
                    }
                }
                finally
                {
                    // The observer forwards main messages to SDL, which owns closing
                    // this handle. Detached HWNDs consume and close it exactly once.
                    if (hwnd != mainWindow) CloseTouchInputHandle(lparam);
                }
                return true;
            case 0x245: case 0x246: case 0x247: case 0x249: case 0x24A: case 0x24C:
                var id = (uint)(wparam & 0xFFFF);
                if (message == 0x24C)
                {
                    if (primaryPointer == id) ReleaseNativePointer(io, true);
                    return true;
                }
                if (!GetPointerInfo(id, out var pointer) || pointer.Type is not (2 or 3)) return false;
                if ((pointer.Flags & 0x2000) == 0) return true; // Dear ImGui has one primary pointer.
                if ((pointer.Flags & 0x8000) != 0)
                { if (primaryPointer == id) ReleaseNativePointer(io, true); return true; }
                io.AddMouseSourceEvent(pointer.Type == 2 ? ImGuiMouseSource.TouchScreen : ImGuiMouseSource.Pen);
                io.AddMousePosEvent(pointer.Pixel.X, pointer.Pixel.Y);
                io.AddMouseViewportEvent(windowIds.GetValueOrDefault(hwnd));
                var contact = message != 0x247 && message != 0x24A && (pointer.Flags & 4) != 0;
                if (contact) { primaryPointer = id; pointerWindow = hwnd; }
                blockedMouse[0] = blockedMouse[1] = false;
                MouseButton(io, 0, contact);
                if (pointer.Type == 3) MouseButton(io, 1, contact && (pointer.Flags & 0x20) != 0);
                if (!contact && primaryPointer == id)
                {
                    primaryPointer = null; pointerWindow = 0;
                    // Promoted mouse state can lag the primary pointer-up message.
                    blockedMouse[0] = blockedMouse[1] = true;
                }
                if (message == 0x24A && !contact) io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
                return true;
            case 0x200: case 0xA0: // WM_MOUSEMOVE / WM_NCMOUSEMOVE
                var source = CurrentMouseSource();
                if (source != ImGuiMouseSource.Mouse) return false; // WM_POINTER is authoritative for promoted input.
                io.AddMouseSourceEvent(source);
                var position = new Point { X = (short)(lparam.ToInt64() & 0xFFFF), Y = (short)((lparam.ToInt64() >> 16) & 0xFFFF) };
                if (message == 0x200) ClientToScreen(hwnd, ref position);
                io.AddMousePosEvent(position.X, position.Y); io.AddMouseViewportEvent(windowIds.GetValueOrDefault(hwnd));
                if (trackingMouse.Add(hwnd))
                {
                    var track = new TrackMouse { Size = (uint)Marshal.SizeOf<TrackMouse>(), Flags = 2, Window = hwnd };
                    if (!TrackMouseEvent(ref track)) trackingMouse.Remove(hwnd);
                }
                return false;
            case 0x2A3: // WM_MOUSELEAVE: crossing another editor HWND is not focus loss.
                trackingMouse.Remove(hwnd);
                if (GetCapture() == 0 && primaryPointer == null && GetCursorPos(out var cursor) && !windowIds.ContainsKey(WindowFromPoint(cursor)))
                { io.AddMousePosEvent(-float.MaxValue, -float.MaxValue); io.AddMouseViewportEvent(0); }
                return false;
            case 0x20A: case 0x20E:
                io.AddMouseSourceEvent(CurrentMouseSource());
                var amount = (short)(wparam >> 16) / 120f;
                if (amount != 0 && IsEditorFocused)
                {
                    var wheelPoint = new Point { X = (short)lparam, Y = (short)(lparam >> 16) };
                    // Queue each real message in sequence; opposite messages
                    // and large deltas remain distinct native input events.
                    io.AddMousePosEvent(wheelPoint.X, wheelPoint.Y);
                    io.AddMouseViewportEvent(windowIds.GetValueOrDefault(WindowFromPoint(wheelPoint)));
                    io.AddMouseWheelEvent(message == 0x20E ? -amount : 0, message == 0x20A ? amount : 0);
                }
                return true;
            case 0x201: case 0x204: case 0x207: case 0x20B:
                if (CurrentMouseSource() != ImGuiMouseSource.Mouse) return false;
                io.AddMouseSourceEvent(ImGuiMouseSource.Mouse);
                var button = NativeMouseButton(message, wparam); blockedMouse[button] = false;
                MouseButton(io, button, true);
                if (GetCapture() == 0) SetCapture(hwnd);
                return false;
            case 0x202: case 0x205: case 0x208: case 0x20C:
                if (CurrentMouseSource() != ImGuiMouseSource.Mouse) return false;
                io.AddMouseSourceEvent(ImGuiMouseSource.Mouse);
                var released = NativeMouseButton(message, wparam); blockedMouse[released] = false;
                MouseButton(io, released, false);
                if (!mouseStates.Any(down => down) && GetCapture() == hwnd) ReleaseCapture();
                return false;
            case 0x215: // WM_CAPTURECHANGED, including a native dialog taking the pointer.
                if (!windowIds.ContainsKey(lparam)) ReleaseNativePointer(io, true);
                return false;
            case 0x1F: // WM_CANCELMODE
                ReleaseNativePointer(io, true);
                if (GetCapture() == hwnd) ReleaseCapture();
                return false;
            case 0x08: // WM_KILLFOCUS
                if (!windowIds.ContainsKey((nint)wparam))
                {
                    ReleaseNativePointer(io, true);
                    if (windowIds.ContainsKey(GetCapture())) ReleaseCapture();
                    foreach (var mapping in NativeKeys) Key(io, mapping.Key, false);
                    Key(io, ImGuiKey.KeypadEnter, false);
                    io.AddFocusEvent(false); focused = false;
                    DiscardQueuedWheelEvents(); text.Clear();
                }
                return false;
            case 0x82: // WM_NCDESTROY
                trackingMouse.Remove(hwnd);
                if (pointerWindow == hwnd) ReleaseNativePointer(io, true);
                return false;
        }
        return false;
    }
    private void ReleaseNativePointer(ImGuiIOPtr io, bool blockHeldButtons)
    {
        EndCameraCapture();
        primaryPointer = null; pointerWindow = 0;
        for (var button = 0; button < mouseStates.Length; button++)
        {
            if (blockHeldButtons && mouseStates[button]) blockedMouse[button] = true;
            MouseButton(io, button, false);
        }
    }
    private static ImGuiMouseSource CurrentMouseSource()
    {
        return GetCurrentInputMessageSource(out var source) ? source.Device switch
        { 4 => ImGuiMouseSource.TouchScreen, 8 => ImGuiMouseSource.Pen, _ => ImGuiMouseSource.Mouse } : ImGuiMouseSource.Mouse;
    }
    // WinUser.h POINTER_INFO / INPUT_MESSAGE_SOURCE / TRACKMOUSEEVENT (SDK 10.0.26100).
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo
    {
        public uint Type, Id, Frame, Flags;
        public nint Device, Window;
        public Point Pixel, Himetric, PixelRaw, HimetricRaw;
        public uint Time, History;
        public int InputData;
        public uint Keys;
        public ulong Performance;
        public uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)] private struct InputMessageSource { public uint Device, Origin; }
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public uint Size, Flags; public nint Window; public uint HoverTime; }
    [StructLayout(LayoutKind.Sequential)] private struct TouchInfo
    {
        public int X, Y;
        public nint Source;
        public uint Id, Flags, Mask, Time;
        public nuint ExtraInfo;
        public uint Width, Height;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTouchInputInfo(nint handle, uint count, TouchInfo* inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseTouchInputHandle(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPointerInfo(uint id, out PointerInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TrackMouseEvent(ref TrackMouse value);
}
