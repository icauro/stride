using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class WindowsMouseCursor : IDisposable
{
    private readonly Dictionary<ImGuiMouseCursor, nint> cursors = [];
    private readonly Dictionary<string, nint> artwork = [];
    private static string? requestedArtwork;
    private static int requestedFrame = -1;
    private static nint requestedWindow;
    private bool disposed;
    internal static unsafe void RequestArtwork(string name)
    {
        if (name is not ("sprite-color-picker" or "sprite-magic-wand")) throw new ArgumentOutOfRangeException(nameof(name));
        requestedArtwork = name; requestedFrame = Gui.GetFrameCount();
        requestedWindow = (nint)Gui.GetWindowViewport().PlatformHandle;
        if (requestedWindow == 0 && GetCursorPos(out var point)) requestedWindow = WindowFromPoint(point);
        Gui.SetMouseCursor(ImGuiMouseCursor.Arrow);
    }
    public WindowsMouseCursor()
    {
        foreach (var (cursor, resource) in new[]
        {
            (ImGuiMouseCursor.Arrow, 32512), (ImGuiMouseCursor.TextInput, 32513),
            (ImGuiMouseCursor.ResizeAll, 32646), (ImGuiMouseCursor.ResizeNs, 32645),
            (ImGuiMouseCursor.ResizeEw, 32644), (ImGuiMouseCursor.ResizeNesw, 32643),
            (ImGuiMouseCursor.ResizeNwse, 32642), (ImGuiMouseCursor.Hand, 32649), (ImGuiMouseCursor.NotAllowed, 32648),
            (ImGuiMouseCursor.Wait, 32514), (ImGuiMouseCursor.Progress, 32650),
        }) cursors[cursor] = LoadCursorW(0, resource);
        Gui.GetIO().BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
    }
    public void Update(nint window, bool active)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (window == 0 || !active || (Gui.GetIO().ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0 || !GetCursorPos(out var point)) return;
        if (GetAncestor(WindowFromPoint(point), 2) != GetAncestor(window, 2)) return;
        var cursor = Gui.GetMouseCursor();
        var native = cursors.GetValueOrDefault(cursor, cursors[ImGuiMouseCursor.Arrow]);
        if (cursor == ImGuiMouseCursor.Arrow && requestedFrame == Gui.GetFrameCount() && requestedArtwork is { } name
            && GetAncestor(window, 2) == GetAncestor(requestedWindow, 2))
        {
            if (!artwork.TryGetValue(name, out native))
            {
                var file = Path.Combine(AppContext.BaseDirectory, "Icons", name + ".cur");
                // Original Cursor(Stream) uses scaleWithDpi=false: retain the
                // embedded CUR's dimensions and hotspot on every monitor.
                native = LoadImageW(0, file, 2, 0, 0, 0x10);
                if (native == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not load the native sprite tool cursor.");
                artwork.Add(name, native);
            }
        }
        SetCursor(Gui.GetIO().MouseDrawCursor || cursor == ImGuiMouseCursor.None ? 0 : native);
    }
    public void Dispose()
    {
        if (disposed) return;
        if (artwork.Values.Contains(GetCursor())) SetCursor(cursors[ImGuiMouseCursor.Arrow]);
        List<Exception>? failures = null;
        foreach (var key in artwork.Keys.ToArray())
        {
            if (DestroyCursor(artwork[key])) artwork.Remove(key);
            else (failures ??= []).Add(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not release the native sprite tool cursor."));
        }
        if (failures != null) throw new AggregateException(failures);
        disposed = true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadCursorW(nint instance, nint resource);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint GetCursor();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyCursor(nint cursor);
}
