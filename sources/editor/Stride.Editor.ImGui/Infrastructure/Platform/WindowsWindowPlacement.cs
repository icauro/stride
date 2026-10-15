using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal sealed class EditorWindowPlacement
{
    public double WidthDip { get; set; }
    public double HeightDip { get; set; }
    public double WorkAreaWidthDip { get; set; }
    public double WorkAreaHeightDip { get; set; }
    public bool Maximized { get; set; }
    public string? MonitorDevice { get; set; }
    internal bool IsValid => double.IsFinite(WidthDip) && WidthDip >= 320 && WidthDip < 100000
        && double.IsFinite(HeightDip) && HeightDip >= 240 && HeightDip < 100000
        && double.IsFinite(WorkAreaWidthDip) && WorkAreaWidthDip > 0 && WorkAreaWidthDip < 100000
        && double.IsFinite(WorkAreaHeightDip) && WorkAreaHeightDip > 0 && WorkAreaHeightDip < 100000;
}

/// <summary>Default and saved editor sizing in physical Win32 coordinates.</summary>
internal static class WindowsWindowPlacement
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public uint Length, Flags, ShowCommand;
        public NativePoint Minimum, Maximum;
        public NativeRect Normal;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect bounds, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    private static MonitorInfo ReadMonitor(nint monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Unable to read the editor window monitor.");
        return info;
    }
    private static double Scale(nint monitor, nint window)
    {
        try { if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0) return x / 96d; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        return WindowsDpi.GetScale(window);
    }
    internal static Stride.Core.Mathematics.Int2 ClientSize(nint window)
    {
        if (!OperatingSystem.IsWindows() || window == 0 || IsIconic(window) || !GetClientRect(window, out var bounds)) return default;
        return new(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
    internal static (int Width, int Height) DefaultBackBufferSize()
    {
        if (!OperatingSystem.IsWindows()) return (1280, 800);
        var info = ReadMonitor(MonitorFromWindow(0, 1));
        return (Math.Max(1, Math.Min((info.Monitor.Right - info.Monitor.Left) / 2, info.Work.Right - info.Work.Left)),
            Math.Max(1, Math.Min((info.Monitor.Bottom - info.Monitor.Top) / 2, info.Work.Bottom - info.Work.Top)));
    }

    internal static void Restore(nint window, EditorWindowPlacement? saved)
    {
        if (!OperatingSystem.IsWindows() || !IsWindow(window)) return;
        if (saved?.IsValid != true) saved = null;
        var monitor = MonitorFromWindow(window, 2);
        if (!string.IsNullOrEmpty(saved?.MonitorDevice))
        {
            MonitorCallback find = (nint candidate, nint dc, ref NativeRect bounds, nint parameter) =>
            {
                var candidateInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfo(candidate, ref candidateInfo) || !string.Equals(candidateInfo.Device, saved.MonitorDevice, StringComparison.OrdinalIgnoreCase)) return true;
                monitor = candidate; return false;
            };
            EnumDisplayMonitors(0, 0, find, 0); GC.KeepAlive(find);
        }
        var info = ReadMonitor(monitor); var scale = Scale(monitor, window);
        var workWidth = info.Work.Right - info.Work.Left; var workHeight = info.Work.Bottom - info.Work.Top;
        if (workWidth <= 0 || workHeight <= 0) return;
        var width = saved == null ? Math.Clamp((info.Monitor.Right - info.Monitor.Left) / 2, 1, workWidth)
            : (int)Math.Round(Math.Clamp(saved.WidthDip * scale, Math.Min(320 * scale, workWidth), workWidth));
        var height = saved == null ? Math.Clamp((info.Monitor.Bottom - info.Monitor.Top) / 2, 1, workHeight)
            : (int)Math.Round(Math.Clamp(saved.HeightDip * scale, Math.Min(240 * scale, workHeight), workHeight));
        var x = info.Work.Left + (workWidth - width) / 2; var y = info.Work.Top + (workHeight - height) / 2;
        if (!SetWindowPos(window, 0, x, y, width, height, 0x14))
            throw new InvalidOperationException("Unable to restore the editor window bounds.");
        var maximize = saved != null && (saved.Maximized || saved.WorkAreaWidthDip > workWidth / scale + 1 || saved.WorkAreaHeightDip > workHeight / scale + 1);
        if (maximize && !WindowsBackgroundLaunch.Enabled) ShowWindow(window, 3);
    }
    internal static EditorWindowPlacement? Capture(nint window)
    {
        if (!OperatingSystem.IsWindows() || !IsWindow(window)) return null;
        var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(window, ref placement)) return null;
        var monitor = MonitorFromWindow(window, 2); var info = ReadMonitor(monitor); var scale = Scale(monitor, window);
        var width = placement.Normal.Right - placement.Normal.Left; var height = placement.Normal.Bottom - placement.Normal.Top;
        if (width <= 0 || height <= 0) return null;
        return new EditorWindowPlacement
        {
            WidthDip = Math.Max(320, width / scale), HeightDip = Math.Max(240, height / scale),
            WorkAreaWidthDip = (info.Work.Right - info.Work.Left) / scale,
            WorkAreaHeightDip = (info.Work.Bottom - info.Work.Top) / scale,
            // Preserve restore-to-maximized when shutdown happens while minimized.
            Maximized = placement.ShowCommand == 3 || (placement.ShowCommand is 2 or 6 or 7) && (placement.Flags & 2) != 0,
            MonitorDevice = info.Device
        };
    }
}

internal sealed partial class EditorGame
{
    private EditorWindowPlacement? lastMainWindowPlacement;
    private long nextPlacementCapture;
    private bool mainWindowPlacementPending;

    private void InitializeMainWindowPlacement()
    {
        if (smokeTest || measureUiControls) return;
        if (args.Contains("--native-default-placement-test")
            && (!args.Contains("--native-input-test") || !args.Contains("--layout-file")))
            throw new ArgumentException("Native default placement requires an isolated native input fixture and explicit private layout file.");
        mainWindowPlacementPending = true;
        try
        {
            WindowsWindowPlacement.Restore(Window.NativeWindow.Handle, preferences?.Current.WindowPlacement);
            SynchronizeMainWindowSize();
            lastMainWindowPlacement = WindowsWindowPlacement.Capture(Window.NativeWindow.Handle);
        }
        catch (Exception error) { Console.Error.WriteLine("Editor window placement: " + error.Message); }
    }

    private void PumpMainWindowPlacement()
    {
        if (mainWindowPlacementPending && GraphicsDevice?.Presenter?.BackBuffer != null)
        {
            mainWindowPlacementPending = false;
            try { WindowsWindowPlacement.Restore(Window.NativeWindow.Handle, preferences?.Current.WindowPlacement); }
            catch (Exception error) { Console.Error.WriteLine("Editor window placement: " + error.Message); }
        }
        SynchronizeMainWindowSize();
        if (!UseUserPreferences || Environment.TickCount64 < nextPlacementCapture) return;
        nextPlacementCapture = Environment.TickCount64 + 500;
        try { lastMainWindowPlacement = WindowsWindowPlacement.Capture(Window.NativeWindow.Handle) ?? lastMainWindowPlacement; }
        catch (Exception error) { Console.Error.WriteLine("Editor window placement: " + error.Message); }
    }

    private void SynchronizeMainWindowSize()
    {
        var nativeSize = WindowsWindowPlacement.ClientSize(Window.NativeWindow.Handle);
        if (nativeSize.X <= 0 || nativeSize.Y <= 0) return;
        var backBuffer = GraphicsDevice?.Presenter?.BackBuffer;
        if (backBuffer != null && backBuffer.Width == nativeSize.X && backBuffer.Height == nativeSize.Y) return;
        // SetSize updates the actual platform window and emits Stride's resize event;
        // it also covers a native SetWindowPos before graphics device creation.
        Window.SetSize(nativeSize);
    }

    private void SaveMainWindowPlacement()
    {
        if (preferences == null || preferences.IsBlocked) return;
        try { lastMainWindowPlacement = WindowsWindowPlacement.Capture(Window.NativeWindow.Handle) ?? lastMainWindowPlacement; }
        catch (Exception error) { Console.Error.WriteLine("Editor window placement: " + error.Message); }
        if (lastMainWindowPlacement?.IsValid == true) preferences.Current.WindowPlacement = lastMainWindowPlacement;
    }
}
