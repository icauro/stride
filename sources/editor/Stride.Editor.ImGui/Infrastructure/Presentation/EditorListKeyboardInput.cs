using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class EditorListKeyboardInput
{
    private static readonly ImGuiKey[] Keys = [ImGuiKey.UpArrow, ImGuiKey.DownArrow, ImGuiKey.LeftArrow, ImGuiKey.RightArrow,
        ImGuiKey.Home, ImGuiKey.End, ImGuiKey.PageUp, ImGuiKey.PageDown, ImGuiKey.Space, ImGuiKey.Backspace, ImGuiKey.Slash, ImGuiKey.Backslash];
    internal static void Claim(string identity)
    {
        var owner = Gui.GetID(identity);
        foreach (var key in Keys) ImGuiP.SetKeyOwner(key, owner);
    }
}
