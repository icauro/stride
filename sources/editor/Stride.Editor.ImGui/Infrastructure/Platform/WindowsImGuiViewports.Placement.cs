using System.Text.Json;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsImGuiViewports
{
    private sealed record SavedPlacement(string Monitor, float X, float Y, float Width, float Height);
    private readonly Dictionary<uint, SavedPlacement> placements = [];
    private readonly string placementFile;
    private void LoadPlacements()
    {
        try
        {
            if (!File.Exists(placementFile)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<uint, SavedPlacement>>(File.ReadAllText(placementFile));
            if (saved != null) foreach (var item in saved) placements[item.Key] = item.Value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { reportError("Viewport placements: " + error.Message); }
    }
    private void SavePlacement(Window window)
    {
        if (IsIconic(window.Handle)) return;
        var origin = new Point(); ClientToScreen(window.Handle, ref origin); GetClientRect(window.Handle, out var bounds);
        var monitor = MonitorFromPoint(origin, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var scale = WindowsDpi.GetScale(window.Handle);
        placements[window.Id] = new(info.Device, (origin.X - info.Work.Left) / scale, (origin.Y - info.Work.Top) / scale,
            bounds.Right / scale, bounds.Bottom / scale);
    }
    private void SavePlacements()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(placementFile)!);
            var temporary = placementFile + "." + Environment.ProcessId + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(placements)); File.Move(temporary, placementFile, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { reportError("Viewport placements: " + error.Message); }
    }
    private void RestorePlacement(uint id, ref Vector2 position, ref Vector2 size)
    {
        var monitor = MonitorFromPoint(new Point { X = (int)position.X, Y = (int)position.Y }, 2);
        if (placements.TryGetValue(id, out var saved))
        {
            MonitorProc callback = (nint candidate, nint dc, ref Rect bounds, nint data) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfo(candidate, ref info) || !string.Equals(info.Device, saved.Monitor, StringComparison.OrdinalIgnoreCase)) return true;
                monitor = candidate; return false;
            };
            EnumDisplayMonitors(0, 0, callback, 0); GC.KeepAlive(callback);
        }
        var selected = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfo(monitor, ref selected)) return;
        var scale = 1f;
        try { if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0) scale = x / 96f; }
        catch (EntryPointNotFoundException) { }
        if (saved != null && float.IsFinite(saved.Width) && float.IsFinite(saved.Height) && saved.Width > 0 && saved.Height > 0
            && float.IsFinite(saved.X) && float.IsFinite(saved.Y))
        { size = new(saved.Width * scale, saved.Height * scale); position = new(selected.Work.Left + saved.X * scale, selected.Work.Top + saved.Y * scale); }
        size = Vector2.Clamp(size, new(64 * scale), new(selected.Work.Right - selected.Work.Left, selected.Work.Bottom - selected.Work.Top));
        position = Vector2.Clamp(position, new(selected.Work.Left, selected.Work.Top), new(selected.Work.Right - size.X, selected.Work.Bottom - size.Y));
    }
}
