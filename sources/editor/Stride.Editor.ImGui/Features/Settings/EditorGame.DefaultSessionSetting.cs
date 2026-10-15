using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawDefaultSessionSetting()
    {
        BeginSettingsProperty("Default session to load");
        var buttonWidth = Gui.GetFrameHeight();
        Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - buttonWidth - Gui.GetStyle().ItemSpacing.X));
        Gui.InputText("##Value", ref settingsDefaultSessionPath, 32768);
        Gui.SameLine();
        if (icons != null && Gui.ImageButton("##Browse", icons.Get("folder-open"),
            new System.Numerics.Vector2(Math.Max(1, buttonWidth - Gui.GetStyle().FramePadding.X * 2))))
        {
            try
            {
                var directory = string.IsNullOrWhiteSpace(settingsDefaultSessionPath) ? preferences?.Current.DialogDirectories.GetValueOrDefault("project")
                    : Path.GetDirectoryName(Path.GetFullPath(settingsDefaultSessionPath));
                var selected = WindowsFileDialogs.Open(Window.NativeWindow.Handle, "Open Stride project or solution",
                    "Stride projects and solutions\0*.sdpkg;*.csproj;*.sln;*.slnx\0Solutions\0*.sln;*.slnx\0Stride packages\0*.sdpkg\0C# projects\0*.csproj\0All files\0*.*\0\0", directory, settingsDefaultSessionPath);
                if (selected != null) settingsDefaultSessionPath = selected;
            }
            catch (Exception error) { editorSettingsError = error.Message; }
        }
        Gui.PopID();
    }
}
