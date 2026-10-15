using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>The reset-camera strip shared by the native ModelPreviewView registrations.</summary>
internal static class PreviewModelToolbar
{
    internal static bool Draw(EditorIcons icons, UiControls controls, string controlId)
    {
        var dpi = Gui.GetStyle().FontScaleDpi;
        var inset = 6 * dpi;
        var buttonHeight = 16 * dpi + Gui.GetStyle().FramePadding.Y * 2;
        using var spacing = ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        using var padding = ImGuiScopes.Style(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var toolbar = ImGuiScopes.Child("##ModelPreviewToolbar", new Vector2(0, buttonHeight + inset * 2),
            ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!toolbar.DrawContents) return false;
        Gui.SetCursorScreenPos(Gui.GetCursorScreenPos() + new Vector2(inset));
        var reset = icons.Button("camera-reset", EditorLocalization.Text("Reset the camera", "ToolTip"), 16);
        if (controls.Enabled) controls.Record(controlId);
        return reset;
    }
}
