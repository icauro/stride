using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class EditorProjectDialogSizing
{
    internal readonly record struct Metrics(float Scale, System.Numerics.Vector2 WorkSize);

    internal static Metrics Resolve(string title)
    {
        var viewport = Gui.GetWindowViewport();
        var window = ImGuiP.FindWindowByName(title);
        if (!window.IsNull && window.WasActive)
        {
            var ownedViewport = Gui.FindViewportByID(window.ViewportId);
            if (!ownedViewport.IsNull) viewport = ownedViewport;
        }
        var scale = Gui.GetIO().ConfigDpiScaleFonts ? viewport.DpiScale : Gui.GetStyle().FontScaleDpi;
        if (!float.IsFinite(scale) || scale <= 0) scale = Gui.GetStyle().FontScaleDpi;
        return new(scale, viewport.WorkSize);
    }

    internal static bool IsUserResizing()
    {
        var window = ImGuiP.GetCurrentWindow();
        if (window.ResizeBorderHeld >= 0) return true;
        var active = ImGuiP.GetActiveID();
        return active != 0 && Gui.IsMouseDown(ImGuiMouseButton.Left)
            && (active == ImGuiP.GetWindowResizeCornerID(window, 0) || active == ImGuiP.GetWindowResizeCornerID(window, 1));
    }
}
