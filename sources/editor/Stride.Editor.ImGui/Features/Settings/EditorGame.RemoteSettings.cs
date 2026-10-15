using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRemoteSettingsDraft()
    {
        DrawRemoteStringSetting("Host", ref settingsRemoteHost);
        BeginSettingsProperty("Port"); Gui.SetNextItemWidth(-1);
        Gui.InputInt("##Value", ref settingsRemotePort); Gui.PopID();
        DrawRemoteStringSetting("Username", ref settingsRemoteUsername);
        DrawRemoteStringSetting("Password", ref settingsRemotePassword, ImGuiInputTextFlags.Password);
        DrawRemoteStringSetting("Location", ref settingsRemoteLocation);
        DrawSettingsBoolean("Ask for credentials when deploying game", ref settingsAskForRemoteCredentials);
        DrawRemoteStringSetting("X Display", ref settingsRemoteDisplay);
    }

    private void DrawRemoteStringSetting(string label, ref string value, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        BeginSettingsProperty(label); Gui.SetNextItemWidth(-1);
        NativeStringInput.Draw("##Value", ref value, flags); RecordSettingsParityInput(label); Gui.PopID();
    }
}
