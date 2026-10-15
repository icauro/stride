using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Win32 platform windows; all ImGui pointers are resolved in the current actor frame.</summary>
internal sealed unsafe partial class WindowsImGuiViewports : IDisposable
{
    private sealed class Window(uint id, nint handle, uint style, uint extended)
    {
        public readonly uint Id = id, Style = style, Extended = extended;
        public readonly nint Handle = handle;
        public EditorViewportPresenter? Presenter;
        public WindowsFileDrop? FileDrop;
    }
    private GraphicsDevice device;
    private readonly nint mainWindow;
    private readonly uint mainViewport;
    private readonly ImGuiContextPtr context;
    private readonly Dictionary<uint, Window> windows = [];
    private readonly HashSet<uint> renderedCaptureViewports = [];
    private readonly Dictionary<nint, uint> windowIds = [];
    private readonly List<Delegate> callbacks = [];
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PlatformWindowVector(ImGuiViewport* viewport, Vector2* result);
    private delegate* unmanaged[Cdecl]<ImGuiPlatformIO*, void*, void> setWindowPositionCallback;
    private delegate* unmanaged[Cdecl]<ImGuiPlatformIO*, void*, void> setWindowSizeCallback;
    private readonly WindowProc windowProc;
    private readonly string className = "Stride.ImGui.Viewport." + Guid.NewGuid().ToString("N");
    private readonly nint instance = GetModuleHandle(null);
    private readonly void* originalIme;
    private readonly void* originalViewportChanged;
    private readonly Action<float>? applyViewportMetrics;
    private readonly Action<string> reportError;
    private readonly WindowsMouseCursor mouseCursor;
    private Exception? callbackFailure;
    private GraphicsContext? renderContext;
    private ImGuiRenderer? renderer;
    private PixelFormat frameBackBufferFormat;
    private bool disposed, focused;
    private bool monitorsDirty = true;
    private readonly Queue<(uint Code, bool Utf16)> text = [];
    private readonly Dictionary<ImGuiKey, bool> keyStates = [];
    private readonly bool[] mouseStates = new bool[5];
    private readonly bool syntheticInput;

    public WindowsImGuiViewports(GraphicsDevice device, nint mainWindow, Action<string> reportError, Action<float>? applyViewportMetrics = null, string? placementFile = null, bool syntheticInput = false)
    {
        if (!OperatingSystem.IsWindows() || mainWindow == 0) throw new PlatformNotSupportedException("Native Windows viewports require the editor HWND.");
        this.device = device; this.mainWindow = mainWindow; this.reportError = reportError;
        this.syntheticInput = syntheticInput;
        this.applyViewportMetrics = applyViewportMetrics;
        this.placementFile = placementFile == null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "viewport-placements.json")
            : Path.GetFullPath(placementFile);
        context = Gui.GetCurrentContext(); mainViewport = Gui.GetMainViewport().ID;
        mouseCursor = new WindowsMouseCursor();
        windowIds.Add(mainWindow, mainViewport);
        windowProc = ProcessMessage;
        var nativeClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(),
            Proc = Marshal.GetFunctionPointerForDelegate(windowProc), Instance = instance, Name = className };
        if (RegisterClassEx(ref nativeClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var platform = Gui.GetPlatformIO(); originalIme = platform.PlatformSetImeDataFn;
        originalViewportChanged = platform.PlatformOnChangedViewport;
        if (applyViewportMetrics != null) platform.PlatformOnChangedViewport = Root<PlatformOnChangedViewport>(ChangedViewport);
        platform.PlatformCreateWindow = Root<PlatformCreateWindow>(Create);
        platform.PlatformDestroyWindow = Root<PlatformDestroyWindow>(Destroy);
        platform.PlatformShowWindow = Root<PlatformShowWindow>(Show);
        platform.PlatformSetWindowPos = Root<PlatformSetWindowPos>(SetPosition);
        RegisterPlatformVectorCallbacks(platform);
        platform.PlatformSetWindowSize = Root<PlatformSetWindowSize>(SetSize);
        platform.PlatformSetWindowFocus = Root<PlatformSetWindowFocus>(viewport =>
        { if (!WindowsBackgroundLaunch.Enabled) SetForegroundWindow(Handle(viewport)); });
        platform.PlatformGetWindowFocus = Root<PlatformGetWindowFocus>(viewport => (byte)(GetForegroundWindow() == Handle(viewport) ? 1 : 0));
        platform.PlatformGetWindowMinimized = Root<PlatformGetWindowMinimized>(viewport => (byte)(IsIconic(Handle(viewport)) ? 1 : 0));
        platform.PlatformSetWindowTitle = Root<PlatformSetWindowTitle>((viewport, title) => SetWindowText(Handle(viewport), Marshal.PtrToStringUTF8((nint)title) ?? ""));
        platform.PlatformSetWindowAlpha = Root<PlatformSetWindowAlpha>(SetAlpha);
        platform.PlatformGetWindowDpiScale = Root<PlatformGetWindowDpiScale>(viewport => WindowsDpi.GetScale(Handle(viewport)));
        // Win32 client and backbuffer dimensions are physical pixels. ImGui's
        // existing framebuffer-scale value is 1; its optional struct-returning
        // callback has no C-compatible bridge in this binding and must stay null.
        platform.PlatformGetWindowFramebufferScale = null;
        platform.PlatformSetImeDataFn = Root<PlatformSetImeDataFn>(SetIme);
        platform.RendererCreateWindow = Root<RendererCreateWindow>(CreateRenderer);
        platform.RendererDestroyWindow = Root<RendererDestroyWindow>(DestroyRenderer);
        platform.RendererSetWindowSize = Root<RendererSetWindowSize>((viewport, size) => ResizeRenderer(viewport, size));
        platform.RendererRenderWindow = Root<RendererRenderWindow>(RenderWindow);
        // Present is deliberately deferred until GameBase.EndDraw flushed every viewport.
        platform.RendererSwapBuffers = Root<RendererSwapBuffers>((viewport, data) => { });
        var main = Gui.GetMainViewport(); main.PlatformHandle = (void*)mainWindow; main.PlatformHandleRaw = (void*)mainWindow;
        var io = Gui.GetIO(); io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports | ImGuiBackendFlags.RendererHasViewports;
        // The isolated synthetic driver supplies recorded control viewport events;
        // production obtains the same identity from native pointer messages.
        io.BackendFlags |= ImGuiBackendFlags.HasMouseHoveredViewport;
        if (!syntheticInput) io.BackendFlags |= ImGuiBackendFlags.HasSetMousePos;
        io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
        io.ConfigDpiScaleViewports = true; io.ConfigDpiScaleFonts = true;
        RefreshMonitors();
        LoadPlacements();
        mainInputMessages = new WindowsWindowMessages(mainWindow, ObserveMainInput, reportError);
    }
    private void* Root<T>(T callback) where T : Delegate
    { callbacks.Add(callback); return (void*)Marshal.GetFunctionPointerForDelegate(callback); }
    private void RegisterPlatformVectorCallbacks(ImGuiPlatformIOPtr platform)
    {
        // These fields are C++ ImVec2-returning function pointers. Hexa's
        // generated result-first delegates cannot be stored in those fields.
        // The native cimgui setters install C++ thunks and retain ordinary C
        // callbacks with the explicit (viewport, out_vector) ABI instead.
        var module = GetModuleHandle("cimgui.dll");
        if (module == 0 || !NativeLibrary.TryGetExport(module, "igGetCurrentContext", out var current)
            || !NativeLibrary.TryGetExport(module, "ImGuiPlatformIO_Set_Platform_GetWindowPos", out var position)
            || !NativeLibrary.TryGetExport(module, "ImGuiPlatformIO_Set_Platform_GetWindowSize", out var size)
            || ((delegate* unmanaged[Cdecl]<ImGuiContext*>)current)() != context.Handle)
            throw new NotSupportedException("The active ImGui library does not provide matching native viewport callback bridges.");
        setWindowPositionCallback = (delegate* unmanaged[Cdecl]<ImGuiPlatformIO*, void*, void>)position;
        setWindowSizeCallback = (delegate* unmanaged[Cdecl]<ImGuiPlatformIO*, void*, void>)size;
        setWindowPositionCallback(platform.Handle, Root<PlatformWindowVector>(GetPosition));
        setWindowSizeCallback(platform.Handle, Root<PlatformWindowVector>(GetSize));
    }
    private nint Handle(ImGuiViewport* viewport) => (nint)viewport->PlatformHandle;
    private void ChangedViewport(ImGuiViewport* viewport)
    {
        try
        {
            if (originalViewportChanged != null) ((delegate* unmanaged[Cdecl]<ImGuiViewport*, void>)originalViewportChanged)(viewport);
            applyViewportMetrics?.Invoke(viewport->DpiScale);
        }
        catch (Exception error) { callbackFailure ??= error; reportError("Platform viewport: " + error); }
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { callbackFailure ??= error; reportError("Platform viewport: " + error); }
    }
    private void Create(ImGuiViewport* viewport) => Guard(() =>
    {
        var flags = viewport->Flags;
        var style = (flags & ImGuiViewportFlags.NoDecoration) != 0 ? 0x80000000u : 0x00CF0000u;
        var extended = (flags & ImGuiViewportFlags.NoTaskBarIcon) != 0 ? 0x80u : 0x40000u;
        if (WindowsBackgroundLaunch.Enabled) extended |= 0x08000000u;
        if ((flags & ImGuiViewportFlags.TopMost) != 0) extended |= 8;
        if ((flags & ImGuiViewportFlags.NoInputs) != 0) extended |= 0x20;
        var position = viewport->Pos; var size = viewport->Size;
        RestorePlacement(viewport->ID, ref position, ref size);
        var bounds = new Rect { Left = (int)position.X, Top = (int)position.Y,
            Right = (int)(position.X + size.X), Bottom = (int)(position.Y + size.Y) };
        AdjustClientRect(ref bounds, style, extended, WindowsDpi.GetScaleAtPosition((int)position.X, (int)position.Y));
        var parent = Gui.FindViewportByID(viewport->ParentViewportId);
        var owner = parent.IsNull ? mainWindow : (nint)parent.PlatformHandle;
        var hwnd = CreateWindowEx(extended, className, "Stride", style, bounds.Left, bounds.Top,
            Math.Max(1, bounds.Right - bounds.Left), Math.Max(1, bounds.Bottom - bounds.Top), owner, 0, instance, 0);
        if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var window = new Window(viewport->ID, hwnd, style, extended);
        windows.Add(viewport->ID, window); windowIds.Add(hwnd, viewport->ID);
        viewport->PlatformHandle = viewport->PlatformHandleRaw = (void*)hwnd;
        viewport->Pos = position; viewport->Size = size; viewport->DpiScale = WindowsDpi.GetScale(hwnd);
        window.FileDrop = new WindowsFileDrop(hwnd);
    });
    private void Destroy(ImGuiViewport* viewport)
    {
        Guard(() =>
        {
            if (windows.TryGetValue(viewport->ID, out var window)) ReleaseWindow(window);
        });
        // Native ImGui may discard this viewport now. Failed owners remain in the
        // managed dictionary for teardown retry, without retaining its native pointer.
        viewport->PlatformHandle = viewport->PlatformHandleRaw = null;
    }
    private void ReleaseWindow(Window window)
    {
        SavePlacement(window);
        window.Presenter?.Dispose(); window.Presenter = null;
        window.FileDrop?.Dispose(); window.FileDrop = null;
        if (GetCapture() == window.Handle) ReleaseCapture();
        if (IsWindow(window.Handle) && !DestroyWindow(window.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not destroy the editor platform window.");
        windowIds.Remove(window.Handle); windows.Remove(window.Id);
    }
    private void Show(ImGuiViewport* viewport)
    {
        var handle = Handle(viewport);
        ShowWindow(handle, WindowsBackgroundLaunch.Enabled || (viewport->Flags & ImGuiViewportFlags.NoFocusOnAppearing) != 0 ? 4 : 5);
        if (WindowsBackgroundLaunch.Enabled) WindowsBackgroundLaunch.PlaceBehindOtherWindows(handle);
    }
    private void GetPosition(ImGuiViewport* viewport, Vector2* result)
    { var point = new Point(); ClientToScreen(Handle(viewport), ref point); *result = new(point.X, point.Y); }
    private void GetSize(ImGuiViewport* viewport, Vector2* result)
    { GetClientRect(Handle(viewport), out var rect); *result = new(rect.Right, rect.Bottom); }
    private void SetPosition(ImGuiViewport* viewport, Vector2 position)
    {
        if (!windows.TryGetValue(viewport->ID, out var window)) return;
        var rect = new Rect(); AdjustClientRect(ref rect, window.Style, window.Extended, WindowsDpi.GetScaleAtPosition((int)position.X, (int)position.Y));
        SetWindowPos(window.Handle, 0, (int)position.X + rect.Left, (int)position.Y + rect.Top, 0, 0, 0x15);
    }
    private void SetSize(ImGuiViewport* viewport, Vector2 size)
    {
        if (!windows.TryGetValue(viewport->ID, out var window)) return;
        var rect = new Rect { Right = Math.Max(1, (int)size.X), Bottom = Math.Max(1, (int)size.Y) };
        AdjustClientRect(ref rect, window.Style, window.Extended, WindowsDpi.GetScale(window.Handle));
        SetWindowPos(window.Handle, 0, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x16);
    }
    private void SetAlpha(ImGuiViewport* viewport, float alpha)
    {
        var hwnd = Handle(viewport); var style = GetWindowLongPtr(hwnd, -20);
        SetWindowLongPtr(hwnd, -20, alpha < 1 ? style | 0x80000 : style & ~0x80000);
        if (alpha < 1) SetLayeredWindowAttributes(hwnd, 0, (byte)(Math.Clamp(alpha, 0, 1) * 255), 2);
    }
    private void CreateRenderer(ImGuiViewport* viewport) => Guard(() =>
    {
        if (renderContext == null || frameBackBufferFormat == PixelFormat.None)
            throw new InvalidOperationException("Native viewport renderer creation requires the actual current host frame and main backbuffer format.");
        if (windows.TryGetValue(viewport->ID, out var window))
            window.Presenter = new EditorViewportPresenter(device, window.Handle, (int)viewport->Size.X, (int)viewport->Size.Y, frameBackBufferFormat);
    });
    private void DestroyRenderer(ImGuiViewport* viewport) => Guard(() =>
    { if (windows.TryGetValue(viewport->ID, out var window)) { window.Presenter?.Dispose(); window.Presenter = null; } });
    private void ResizeRenderer(ImGuiViewport* viewport, Vector2 size) => Guard(() =>
    { if (windows.TryGetValue(viewport->ID, out var window)) window.Presenter?.Resize((int)size.X, (int)size.Y); });
    private void RenderWindow(ImGuiViewport* viewport, void* data) => Guard(() =>
    {
        if (renderContext != null && renderer != null && windows.TryGetValue(viewport->ID, out var window) && !IsIconic(window.Handle))
        {
            if (window.Presenter?.Render(renderContext, renderer, new ImDrawDataPtr(viewport->DrawData), (viewport->Flags & ImGuiViewportFlags.NoRendererClear) == 0) == true)
                renderedCaptureViewports.Add(viewport->ID);
        }
    });
    public void RenderPlatformWindows(GraphicsContext context, ImGuiRenderer renderer, GraphicsDevice frameDevice, PixelFormat mainBackBufferFormat)
    {
        if (!ReferenceEquals(context.CommandList.GraphicsDevice, frameDevice))
            throw new InvalidOperationException("The native viewport frame device does not own the actual host command list.");
        if (mainBackBufferFormat == PixelFormat.None)
            throw new InvalidOperationException("The native viewport frame has no actual main backbuffer format.");
        if (!ReferenceEquals(device, frameDevice) && windows.Values.Any(window => window.Presenter != null))
            throw new InvalidOperationException("Retire native viewport presenters through graphics recovery before changing their owning device.");
        // Initialize may precede the final main-device generation. Borrow this
        // actual frame's owner before UpdatePlatformWindows dispatches renderer
        // creation; existing GPU owners cannot migrate to another device.
        device = frameDevice;
        frameBackBufferFormat = mainBackBufferFormat;
        this.renderContext = context; this.renderer = renderer;
        renderedCaptureViewports.Clear();
        try { Gui.UpdatePlatformWindows(); Gui.RenderPlatformWindowsDefault(); }
        finally { this.renderContext = null; this.renderer = null; frameBackBufferFormat = PixelFormat.None; }
        if (callbackFailure is { } failure) { callbackFailure = null; System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
    }
    public void PresentAfterFrame(bool present)
    { renderedCaptureViewports.Clear(); foreach (var window in windows.Values) window.Presenter?.PresentAfterFrame(present); }
    internal Texture? GetRenderedCaptureBuffer(uint viewportId)
        => renderedCaptureViewports.Contains(viewportId) && windows.TryGetValue(viewportId, out var window)
            && !IsIconic(window.Handle) ? window.Presenter?.PendingBackBuffer : null;
    public bool TryDequeueFileDrop(out WindowsFileDrop.Drop? drop)
    {
        foreach (var window in windows.Values)
            if (window.FileDrop?.TryDequeue(out var value) == true && value != null)
            {
                var point = new Point { X = value.X, Y = value.Y }; ClientToScreen(window.Handle, ref point);
                drop = new WindowsFileDrop.Drop(value.Files, point.X, point.Y); return true;
            }
        drop = null; return false;
    }
    public System.Numerics.Vector2 MainClientToScreen(int x, int y)
    {
        var point = new Point { X = x, Y = y }; ClientToScreen(mainWindow, ref point); return new(point.X, point.Y);
    }
    private void SetIme(ImGuiContext* nativeContext, ImGuiViewport* viewport, ImGuiPlatformImeData* data)
    {
        if (syntheticInput) return;
        if (viewport == null || data == null) return;
        if (viewport->ID == mainViewport)
        {
            if (originalIme != null) ((delegate* unmanaged[Cdecl]<ImGuiContext*, ImGuiViewport*, ImGuiPlatformImeData*, void>)originalIme)(nativeContext, viewport, data);
            return;
        }
        var hwnd = Handle(viewport); var ime = ImmGetContext(hwnd); if (ime == 0) return;
        try
        {
            if (data->WantTextInput == 0 && data->WantVisible == 0)
            { ImmNotifyIME(ime, 0x15, 4, 0); return; }
            if (!float.IsFinite(data->InputPos.X) || !float.IsFinite(data->InputPos.Y) || !float.IsFinite(data->InputLineHeight)) return;
            // ImGui coordinates and framebuffer sizes are physical screen pixels;
            // native IME APIs require this HWND's physical client coordinates.
            var point = new Point { X = (int)Math.Clamp(data->InputPos.X, -1_000_000, 1_000_000),
                Y = (int)Math.Clamp(data->InputPos.Y, -1_000_000, 1_000_000) };
            if (!ScreenToClient(hwnd, ref point)) return;
            var composition = new CompositionForm { Style = 2, Position = point };
            var candidate = new CandidateForm { Style = 0x80, Position = point,
                Area = new Rect { Left = point.X, Top = point.Y, Right = point.X + 1,
                    Bottom = point.Y + (int)Math.Clamp(MathF.Ceiling(data->InputLineHeight), 1, 1_000_000) } };
            ImmSetCompositionWindow(ime, ref composition); ImmSetCandidateWindow(ime, ref candidate);
        }
        finally { ImmReleaseContext(hwnd, ime); }
    }
    private nint ProcessMessage(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        if (syntheticInput && message is >= 0x100 and <= 0x109) return 0;
        if (!windowIds.TryGetValue(hwnd, out var id)) return DefWindowProc(hwnd, message, wparam, lparam);
        var previous = Gui.GetCurrentContext(); Gui.SetCurrentContext(context);
        try
        {
            if (HandlePointerInput(hwnd, message, wparam, lparam)) return 0;
            var viewport = Gui.FindViewportByID(id);
            if (!viewport.IsNull)
                switch (message)
                {
                    case 0x10: viewport.PlatformRequestClose = true; return 0;
                    case 0x03: viewport.PlatformRequestMove = true; break;
                    case 0x05: viewport.PlatformRequestResize = true; break;
                    case 0x2E0:
                        var rect = Marshal.PtrToStructure<Rect>(lparam);
                        SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
                        viewport.PlatformRequestMove = viewport.PlatformRequestResize = true; monitorsDirty = true; break;
                    case 0x7E: monitorsDirty = true; break;
                    case 0x102: text.Enqueue(((uint)wparam, true)); return 0;
                    case 0x109: // WM_UNICHAR / UNICODE_NOCHAR feature query
                        if (wparam == 0xFFFF) return 1;
                        text.Enqueue(((uint)wparam, false)); return 0;
                    case 0x100: case 0x101: case 0x104: case 0x105:
                        var key = (int)wparam;
                        var scanCode = (int)(((long)lparam >> 16) & 0xFF);
                        var extended = ((long)lparam & (1L << 24)) != 0;
                        if (key == 17) key = extended ? 163 : 162;
                        if (key == 18) key = extended ? 165 : 164;
                        if (key == 16) key = (((long)lparam >> 16) & 0xFF) == 0x36 ? 161 : 160;
                        if (key == 13 && extended)
                        {
                            Key(Gui.GetIO(), ImGuiKey.KeypadEnter, message is 0x100 or 0x104);
                            Gui.GetIO().SetKeyEventNativeData(ImGuiKey.KeypadEnter, key, scanCode);
                        }
                        else foreach (var mapping in NativeKeys)
                            if (mapping.Native == key)
                            {
                                if (key == 44 && message is 0x101 or 0x105) Key(Gui.GetIO(), mapping.Key, true);
                                Key(Gui.GetIO(), mapping.Key, message is 0x100 or 0x104);
                                Gui.GetIO().SetKeyEventNativeData(mapping.Key, key, scanCode); break;
                            }
                        break;
                    case 0x21:
                        if (WindowsBackgroundLaunch.Enabled || (viewport.Flags & ImGuiViewportFlags.NoFocusOnClick) != 0) return 3; break;
                    case 0x84:
                        if ((viewport.Flags & ImGuiViewportFlags.NoInputs) != 0) return -1; break;
                }
        }
        catch (Exception error) { reportError("Viewport input: " + error); }
        finally { Gui.SetCurrentContext(previous); }
        return DefWindowProc(hwnd, message, wparam, lparam);
    }
    public void Dispose()
    {
        if (disposed) return;
        var previous = Gui.GetCurrentContext(); Gui.SetCurrentContext(context);
        try { ReleasePlatformWindows(); }
        finally { Gui.SetCurrentContext(previous); }
    }
    private void ReleasePlatformWindows()
    {
        EndCameraCapture();
        callbackFailure = null;
        Gui.DestroyPlatformWindows();
        var failures = new List<Exception>();
        // ImGui may already have removed a viewport after a native callback failed.
        // Retain managed owners and retry their actual resource release here.
        foreach (var window in windows.Values.ToArray())
            try { ReleaseWindow(window); }
            catch (Exception error) { failures.Add(error); }
        if (windows.Count != 0)
        {
            if (callbackFailure != null) failures.Insert(0, callbackFailure);
            throw new AggregateException("Editor platform windows could not be released; graphics recovery must retry their retained owners.", failures);
        }
        callbackFailure = null;
        mouseCursor.Dispose();
        mainInputMessages?.Dispose(); mainInputMessages = null;
        ReleaseNativePointer(Gui.GetIO(), false);
        if (windowIds.ContainsKey(GetCapture())) ReleaseCapture();
        SavePlacements();
        var platform = Gui.GetPlatformIO();
        // Clear cimgui's retained managed callback pointers before releasing
        // their delegate roots, then clear the C++ thunk fields as well.
        setWindowPositionCallback(platform.Handle, null);
        setWindowSizeCallback(platform.Handle, null);
        platform.PlatformCreateWindow = platform.PlatformDestroyWindow = platform.PlatformShowWindow = null;
        platform.PlatformSetWindowPos = platform.PlatformGetWindowPos = platform.PlatformSetWindowSize = platform.PlatformGetWindowSize = null;
        platform.PlatformSetWindowFocus = platform.PlatformGetWindowFocus = platform.PlatformGetWindowMinimized = null;
        platform.PlatformSetWindowTitle = platform.PlatformSetWindowAlpha = platform.PlatformGetWindowDpiScale = platform.PlatformGetWindowFramebufferScale = null;
        platform.RendererCreateWindow = platform.RendererDestroyWindow = platform.RendererSetWindowSize = platform.RendererRenderWindow = platform.RendererSwapBuffers = null;
        platform.PlatformSetImeDataFn = originalIme;
        if (applyViewportMetrics != null)
        {
            platform.PlatformOnChangedViewport = originalViewportChanged;
            applyViewportMetrics(Gui.GetMainViewport().DpiScale);
        }
        var io = Gui.GetIO(); io.ConfigFlags &= ~ImGuiConfigFlags.ViewportsEnable;
        io.BackendFlags &= ~(ImGuiBackendFlags.PlatformHasViewports | ImGuiBackendFlags.RendererHasViewports | ImGuiBackendFlags.HasMouseHoveredViewport | ImGuiBackendFlags.HasSetMousePos);
        if (!UnregisterClass(className, instance)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not unregister the editor platform window class.");
        disposed = true; callbacks.Clear(); GC.KeepAlive(windowProc);
    }
}
