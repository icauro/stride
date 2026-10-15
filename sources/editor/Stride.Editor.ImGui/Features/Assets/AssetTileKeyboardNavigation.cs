using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class AssetTileKeyboardNavigation
{
    internal static int? ReadTarget(int current, int count, int columns, int pageRows, bool allowModalOwner = false)
    {
        if (current < 0 || count == 0 || !Gui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows)
            || Gui.GetIO().AppFocusLost || Gui.GetIO().WantTextInput || Gui.IsAnyItemActive()
            || Gui.IsPopupOpen("", allowModalOwner ? ImGuiPopupFlags.AnyPopupId : ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return null;
        foreach (var key in Keys)
        {
            if (!Gui.IsKeyPressed(key)) continue;
            ImGuiP.SetKeyOwner(key, Gui.GetID("AssetTileNavigation"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            var next = key switch
            {
                ImGuiKey.LeftArrow => current - 1,
                ImGuiKey.RightArrow => current + 1,
                ImGuiKey.UpArrow => current - columns,
                ImGuiKey.DownArrow => current + columns,
                ImGuiKey.PageUp => current - columns * pageRows,
                ImGuiKey.PageDown => current + columns * pageRows,
                ImGuiKey.Home => 0,
                ImGuiKey.End => count - 1,
                _ => current,
            };
            return next >= 0 && next < count && next != current ? next : null;
        }
        return null;
    }

    private static readonly ImGuiKey[] Keys = [ImGuiKey.LeftArrow, ImGuiKey.RightArrow, ImGuiKey.UpArrow, ImGuiKey.DownArrow,
        ImGuiKey.PageUp, ImGuiKey.PageDown, ImGuiKey.Home, ImGuiKey.End];
}
