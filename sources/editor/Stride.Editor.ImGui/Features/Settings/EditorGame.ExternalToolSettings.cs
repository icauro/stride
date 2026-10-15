using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string settingsTextEditor = "", settingsShaderEditor = "";
    private LogMessageType settingsDebugOutputLevel;
    private string settingsGraphicsApi = "Default";
    private bool settingsRestartNotice;
    private static readonly string[] EditorGraphicsChoices = ["Default", "Direct3D11", "Direct3D12", "Vulkan"];
    private EditorDiagnosticRouting? diagnosticRouting;
    private static readonly string[] DebugOutputLevels = Enum.GetNames<LogMessageType>();

    private void DrawExternalEditorSetting(string label, ref string value)
    {
        BeginSettingsProperty(label);
        var width = Gui.GetFrameHeight();
        Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - width - Gui.GetStyle().ItemSpacing.X));
        Gui.InputText("##Value", ref value, 32768);
        Gui.SameLine();
        if (icons != null && Gui.ImageButton("##Browse", icons.Get("folder-open"),
                new System.Numerics.Vector2(Math.Max(1, width - Gui.GetStyle().FramePadding.X * 2))))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(value);
                var directory = Path.IsPathFullyQualified(expanded) ? Path.GetDirectoryName(expanded) : null;
                var selected = WindowsFileDialogs.Open(GetCurrentDialogOwner(), EditorLocalization.Text(label, "Settings"),
                    "Programs\0*.exe\0All files\0*.*\0\0", directory, expanded);
                if (selected != null) value = selected;
            }
            catch (Exception error) { editorSettingsError = error.Message; }
        }
        Gui.PopID();
    }

    private void DrawDebugOutputLevelSetting()
    {
        BeginSettingsProperty("Debugger output level (Game Studio only)");
        var index = Array.IndexOf(DebugOutputLevels, settingsDebugOutputLevel.ToString());
        Gui.SetNextItemWidth(-1);
        if (Gui.Combo("##Value", ref index, DebugOutputLevels, DebugOutputLevels.Length))
            settingsDebugOutputLevel = Enum.Parse<LogMessageType>(DebugOutputLevels[index]);
        Gui.PopID();
    }

    private void DrawGraphicsApiSetting()
    {
        BeginSettingsProperty("Graphics API (Game Studio only)");
        var index = Array.IndexOf(EditorGraphicsChoices, settingsGraphicsApi);
        Gui.SetNextItemWidth(-1);
        if (Gui.Combo("##Value", ref index, EditorGraphicsChoices, EditorGraphicsChoices.Length))
            settingsGraphicsApi = EditorGraphicsChoices[index];
        Gui.PopID();
    }

    private void DrawSettingsRestartNotice()
    {
        var title = WpfMessageDialogTitle("SettingsRestartNotice");
        if (settingsRestartNotice) { settingsRestartNotice = false; Gui.OpenPopup(title); }
        using var popup = ImGuiScopes.Modal(title, Hexa.NET.ImGui.ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup.DrawContents) return;
        DrawSelectableText("##SettingsRestartMessage", EditorLocalization.Text("Some changes will be applied after you restart Game Studio.", "Message"));
        if (Gui.Button(EditorLocalization.Widget("OK", "Button"))) Gui.CloseCurrentPopup();
    }
}
