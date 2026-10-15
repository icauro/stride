using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ProcessAssetBrowserAddShortcut()
    {
        var io = Gui.GetIO();
        if (!CanAddAsset || io.AppFocusLost || !io.KeyCtrl || io.KeyShift || io.KeyAlt || io.KeySuper || io.WantTextInput
            || Gui.IsAnyItemActive() || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;
        if (Gui.IsKeyPressed(ImGuiKey.I, false)) Execute(RequestAssetCreation);
    }
}
