using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class AssetBrowserZoomGesture
{
    internal static int ReadDirection(bool allowModalOwner = false)
    {
        var io = Gui.GetIO();
        if (io.AppFocusLost || !io.KeyCtrl || io.KeyShift || io.KeyAlt || io.KeySuper
            || io.WantTextInput || Gui.IsAnyItemActive()
            || !Gui.IsWindowHovered(allowModalOwner ? ImGuiHoveredFlags.ChildWindows : ImGuiHoveredFlags.RootAndChildWindows)
            || Gui.IsPopupOpen("", allowModalOwner ? ImGuiPopupFlags.AnyPopupId : ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return 0;
        // Own the wheel even at a zoom limit: WPF handles the gesture there too.
        ImGuiP.SetKeyOwner(ImGuiKey.MouseWheelY, Gui.GetID("AssetThumbnailZoom"));
        var direction = Math.Sign(io.MouseWheel);
        io.MouseWheel = 0;
        return direction;
    }

    internal static int NextSize(int size, int direction) => Math.Clamp(size + direction * 16, 16, 128);
}
