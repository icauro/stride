using Hexa.NET.ImGui;
using Stride.Input;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsImGuiViewports
{
    private static readonly (int Native, ImGuiKey Key)[] NativeKeys = Keys();
    private static readonly int[] NativeButtons = [1, 2, 4, 5, 6];
    private static (int, ImGuiKey)[] Keys()
    {
        var keys = new List<(int, ImGuiKey)> {
            (9, ImGuiKey.Tab), (37, ImGuiKey.LeftArrow), (39, ImGuiKey.RightArrow), (38, ImGuiKey.UpArrow), (40, ImGuiKey.DownArrow),
            (33, ImGuiKey.PageUp), (34, ImGuiKey.PageDown), (36, ImGuiKey.Home), (35, ImGuiKey.End), (45, ImGuiKey.Insert),
            (46, ImGuiKey.Delete), (8, ImGuiKey.Backspace), (32, ImGuiKey.Space), (13, ImGuiKey.Enter), (27, ImGuiKey.Escape),
            (160, ImGuiKey.LeftShift), (161, ImGuiKey.RightShift), (162, ImGuiKey.LeftCtrl), (163, ImGuiKey.RightCtrl),
            (164, ImGuiKey.LeftAlt), (165, ImGuiKey.RightAlt), (91, ImGuiKey.LeftSuper), (92, ImGuiKey.RightSuper), (93, ImGuiKey.Menu),
            (222, ImGuiKey.Apostrophe), (188, ImGuiKey.Comma), (189, ImGuiKey.Minus), (190, ImGuiKey.Period), (191, ImGuiKey.Slash),
            (186, ImGuiKey.Semicolon), (187, ImGuiKey.Equal), (219, ImGuiKey.LeftBracket), (220, ImGuiKey.Backslash),
            (221, ImGuiKey.RightBracket), (192, ImGuiKey.GraveAccent), (226, ImGuiKey.Oem102), (166, ImGuiKey.AppBack), (167, ImGuiKey.AppForward), (20, ImGuiKey.CapsLock), (145, ImGuiKey.ScrollLock),
            (144, ImGuiKey.NumLock), (44, ImGuiKey.PrintScreen), (19, ImGuiKey.Pause), (110, ImGuiKey.KeypadDecimal),
            (111, ImGuiKey.KeypadDivide), (106, ImGuiKey.KeypadMultiply), (109, ImGuiKey.KeypadSubtract), (107, ImGuiKey.KeypadAdd) };
        for (var i = 0; i < 26; i++) keys.Add((65 + i, ImGuiKey.A + i));
        for (var i = 0; i < 10; i++) { keys.Add((48 + i, ImGuiKey.Key0 + i)); keys.Add((96 + i, ImGuiKey.Keypad0 + i)); }
        for (var i = 0; i < 24; i++) keys.Add((112 + i, ImGuiKey.F1 + i));
        return keys.ToArray();
    }
    public bool IsEditorFocused => windowIds.ContainsKey(GetForegroundWindow());
    private object? lastNavigationMouseWarp;
    internal object InspectNativePointer()
    {
        var hasPosition = GetCursorPos(out var cursor);
        GetPhysicalCursorPos(out var physical);
        var hit = hasPosition ? WindowFromPoint(cursor) : 0;
        var root = hit == 0 ? 0 : GetAncestor(hit, 2);
        return new { hasPosition, x = cursor.X, y = cursor.Y, hit = hit.ToInt64(), root = root.ToInt64(),
            foreground = GetForegroundWindow().ToInt64(), capture = GetCapture().ToInt64(),
            hitViewport = windowIds.GetValueOrDefault(hit), rootViewport = windowIds.GetValueOrDefault(root),
            physicalX = physical.X, physicalY = physical.Y, wantSetMousePos = Gui.GetIO().WantSetMousePos,
            guiMouseX = Gui.GetIO().MousePos.X, guiMouseY = Gui.GetIO().MousePos.Y,
            lastNavigationMouseWarp, cameraCaptured = HasCameraCapture, primaryPointer, focused };
    }
    public void NotifyDisplayChanged() => monitorsDirty = true;
    internal void SuspendNativeModalInput()
    {
        EndCameraCapture();
        var io = Gui.GetIO();
        ReleaseNativePointer(io, true);
        foreach (var key in keyStates.Where(pair => pair.Value).Select(pair => pair.Key).ToArray()) Key(io, key, false);
        keyStates.Clear(); focused = false; text.Clear(); DiscardQueuedWheelEvents();
        io.InputQueueSurrogate = 0;
    }
    public void UpdateBeforeNewFrame(InputManager input, float deltaTime)
    {
        Gui.SetCurrentContext(context);
        // Only our child HWND queues are pumped here; Stride retains its main event loop.
        foreach (var window in windows.Values)
            while (PeekMessage(out var message, window.Handle, 0, 0, 1))
            { TranslateMessage(ref message); DispatchMessage(ref message); }
        foreach (var window in windows.Values)
        {
            var viewport = Gui.FindViewportByID(window.Id);
            if (viewport.IsNull) continue;
            // A viewport created during dragging starts NoInputs. This flag is
            // transient; retaining creation-time WS_EX_TRANSPARENT prevents
            // later clicks from reaching that real detached editor window.
            var extended = GetWindowLongPtr(window.Handle, -20).ToInt64();
            var desired = (viewport.Flags & ImGuiViewportFlags.NoInputs) != 0 ? extended | 0x20 : extended & ~0x20;
            if (extended == desired) continue;
            SetWindowLongPtr(window.Handle, -20, (nint)desired);
            if (GetWindowLongPtr(window.Handle, -20).ToInt64() != desired)
                throw new InvalidOperationException("Unable to synchronize native viewport input transparency.");
        }
        CheckCameraCaptureOwnership();
        if (monitorsDirty) RefreshMonitors();
        var io = Gui.GetIO();
        GetClientRect(mainWindow, out var bounds);
        io.DisplaySize = new(bounds.Right, bounds.Bottom); io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = Math.Max(0.001f, deltaTime);
        var main = Gui.GetMainViewport(); var origin = new Point(); ClientToScreen(mainWindow, ref origin);
        main.Pos = new(origin.X, origin.Y); main.Size = io.DisplaySize; main.DpiScale = WindowsDpi.GetScale(mainWindow);
        // Native presentation remains real; this isolated harness supplies all
        // ImGui input and must never poll, capture or warp the desktop pointer.
        if (syntheticInput) return;
        var active = IsEditorFocused;
        if (active && !HasCameraCapture && primaryPointer == null && io.WantSetMousePos && float.IsFinite(io.MousePos.X) && float.IsFinite(io.MousePos.Y))
        {
            GetPhysicalCursorPos(out var beforeWarp);
            var moved = SetCursorPos((int)io.MousePos.X, (int)io.MousePos.Y);
            GetPhysicalCursorPos(out var afterWarp);
            lastNavigationMouseWarp = new { frame = Gui.GetFrameCount(), x = io.MousePos.X, y = io.MousePos.Y,
                beforeX = beforeWarp.X, beforeY = beforeWarp.Y, afterX = afterWarp.X, afterY = afterWarp.Y, moved };
        }
        if (focused != active)
        {
            if (!active)
            {
                ReleaseNativePointer(io, true);
                if (windowIds.ContainsKey(GetCapture())) ReleaseCapture();
                text.Clear(); DiscardQueuedWheelEvents();
                io.InputQueueSurrogate = 0;
            }
            io.AddFocusEvent(active); focused = active;
        }
        if ((!active || (GetAsyncKeyState(13) & 0x8000) == 0) && keyStates.GetValueOrDefault(ImGuiKey.KeypadEnter)) Key(io, ImGuiKey.KeypadEnter, false);
        if (GetForegroundWindow() == mainWindow)
            foreach (var value in input.Events)
            {
                if (value is KeyEvent keyEvent && ImGuiInput.TryMapKey(keyEvent.Key, out var mapped)) Key(io, mapped, keyEvent.IsDown);
            }
        foreach (var (native, key) in NativeKeys)
            Key(io, key, active && (GetAsyncKeyState(native) & 0x8000) != 0 && (native != 13 || !keyStates.GetValueOrDefault(ImGuiKey.KeypadEnter)));
        Key(io, ImGuiKey.ModCtrl, active && (GetAsyncKeyState(17) & 0x8000) != 0);
        Key(io, ImGuiKey.ModShift, active && (GetAsyncKeyState(16) & 0x8000) != 0);
        Key(io, ImGuiKey.ModAlt, active && (GetAsyncKeyState(18) & 0x8000) != 0);
        Key(io, ImGuiKey.ModSuper, active && ((GetAsyncKeyState(91) | GetAsyncKeyState(92)) & 0x8000) != 0);
        for (var i = 0; i < NativeButtons.Length; i++)
        {
            if (primaryPointer != null && i < 2) continue;
            var down = active && (GetAsyncKeyState(NativeButtons[i]) & 0x8000) != 0;
            if (!down) blockedMouse[i] = false;
            down &= !blockedMouse[i];
            MouseButton(io, i, down);
        }
        if (HasCameraCapture) SubmitCameraPointer(io);
        else if (primaryPointer == null && GetCursorPos(out var cursor))
        {
            var hoveredWindow = FindInputWindowAtPoint(cursor);
            if (windowIds.ContainsKey(hoveredWindow) || windowIds.ContainsKey(GetCapture())) io.AddMousePosEvent(cursor.X, cursor.Y);
            else io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
            io.AddMouseViewportEvent(windowIds.GetValueOrDefault(hoveredWindow));
            if (windowIds.ContainsKey(hoveredWindow)) mouseCursor.Update(hoveredWindow, active);
        }
        // Native child WM_CHAR includes committed IME text; main-window text remains
        // supplied by Stride's actual text device, with composition handled separately.
        if (GetForegroundWindow() == mainWindow)
            foreach (var value in input.Events)
            {
                if (value is TextInputEvent { Type: TextInputEventType.Input } committed)
                    foreach (var rune in committed.Text.EnumerateRunes()) io.AddInputCharacter((uint)rune.Value);
            }
        while (text.TryDequeue(out var value))
            if (active)
            {
                if (value.Utf16) io.AddInputCharacterUTF16((ushort)value.Code);
                else io.AddInputCharacter(value.Code);
            }
    }
    private nint FindInputWindowAtPoint(Point cursor)
    {
        var hit = WindowFromPoint(cursor);
        if (!windowIds.TryGetValue(hit, out var id)) return hit;
        var viewport = Gui.FindViewportByID(id);
        if (viewport.IsNull || (viewport.Flags & ImGuiViewportFlags.NoInputs) == 0) return hit;
        // WindowFromPoint does not reliably honor our HTTRANSPARENT callback.
        // ImGui's moving viewport is NoInputs: report the real underlying native
        // window, retaining foreign-window occlusion rather than guessing main.
        for (var below = GetWindow(hit, 2); below != 0; below = GetWindow(below, 2))
        {
            if (!IsWindowVisible(below) || !GetWindowRect(below, out var rect)
                || cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom) continue;
            if (windowIds.TryGetValue(below, out var belowId))
            {
                var belowViewport = Gui.FindViewportByID(belowId);
                if (!belowViewport.IsNull && (belowViewport.Flags & ImGuiViewportFlags.NoInputs) != 0) continue;
            }
            return below;
        }
        return 0;
    }
    private void DiscardQueuedWheelEvents()
    {
        // Keep genuine pointer/key/focus transitions; remove only wheel work
        // invalidated by native modal ownership or editor-wide focus loss.
        var events = context.InputEventsQueue; var written = 0;
        for (var read = 0; read < events.Size; read++)
            if (events[read].Type != ImGuiInputEventType.MouseWheel) events[written++] = events[read];
        events.Resize(written);
    }
    private void Key(ImGuiIOPtr io, ImGuiKey key, bool down)
    {
        if (keyStates.GetValueOrDefault(key) == down) return;
        keyStates[key] = down; io.AddKeyEvent(key, down);
        if (key == ImGuiKey.LeftCtrl || key == ImGuiKey.RightCtrl)
            Key(io, ImGuiKey.ModCtrl, keyStates.GetValueOrDefault(ImGuiKey.LeftCtrl) || keyStates.GetValueOrDefault(ImGuiKey.RightCtrl));
        if (key == ImGuiKey.LeftShift || key == ImGuiKey.RightShift)
            Key(io, ImGuiKey.ModShift, keyStates.GetValueOrDefault(ImGuiKey.LeftShift) || keyStates.GetValueOrDefault(ImGuiKey.RightShift));
        if (key == ImGuiKey.LeftAlt || key == ImGuiKey.RightAlt)
            Key(io, ImGuiKey.ModAlt, keyStates.GetValueOrDefault(ImGuiKey.LeftAlt) || keyStates.GetValueOrDefault(ImGuiKey.RightAlt));
        if (key == ImGuiKey.LeftSuper || key == ImGuiKey.RightSuper)
            Key(io, ImGuiKey.ModSuper, keyStates.GetValueOrDefault(ImGuiKey.LeftSuper) || keyStates.GetValueOrDefault(ImGuiKey.RightSuper));
    }
    private void MouseButton(ImGuiIOPtr io, int button, bool down)
    {
        if (button < 0 || button >= mouseStates.Length || mouseStates[button] == down) return;
        mouseStates[button] = down; io.AddMouseButtonEvent(button, down);
    }
    private static int NativeMouseButton(uint message, nuint wparam) => message switch
    { 0x201 or 0x202 => 0, 0x204 or 0x205 => 1, 0x207 or 0x208 => 2, _ => (wparam >> 16) == 1 ? 3 : 4 };
    private void RefreshMonitors()
    {
        var monitors = new List<ImGuiPlatformMonitor>();
        MonitorProc callback = (nint monitor, nint dc, ref Rect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var scale = 1f;
            try { if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0) scale = x / 96f; }
            catch (EntryPointNotFoundException) { }
            var entry = new ImGuiPlatformMonitor { MainPos = new(info.Monitor.Left, info.Monitor.Top),
                MainSize = new(info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top),
                WorkPos = new(info.Work.Left, info.Work.Top), WorkSize = new(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top),
                DpiScale = scale, PlatformHandle = (void*)monitor };
            if ((info.Flags & 1) != 0) monitors.Insert(0, entry); else monitors.Add(entry);
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0); GC.KeepAlive(callback);
        var platform = Gui.GetPlatformIO(); platform.Monitors.Clear();
        foreach (var monitor in monitors) platform.Monitors.PushBack(monitor);
        monitorsDirty = false;
    }
}
