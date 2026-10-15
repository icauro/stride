using Gui = Hexa.NET.ImGui.ImGui;
using Hexa.NET.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool pendingGridSettingsSave;
    private void DrawGridControls(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport?.Grid is not { } grid) return;
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.IsAnyItemActive() && !Gui.GetIO().WantTextInput
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.G, false))
        { grid.ApplySettings(grid.Settings with { Visible = !grid.Settings.Visible }); if (context == null) pendingGridSettingsSave = true; else context.PendingGridSave = true; }
        if (SceneToolbarIcon("scene-toolbar-grid", "Viewport grid settings...", grid.Settings.Visible)) Gui.OpenPopup("Viewport grid settings");
        if (!Gui.BeginPopup("Viewport grid settings")) { SavePendingGridSettings(context); return; }
        var current = grid.Settings; var visible = current.Visible;
        if (Gui.Checkbox("Show Grid", ref visible)) current = current with { Visible = visible };
        Gui.SeparatorText(EditorLocalization.Text("Grid axis"));
        var x = (current.Axes & 1) != 0; var y = (current.Axes & 2) != 0; var z = (current.Axes & 4) != 0;
        var changed = Gui.Checkbox(EditorLocalization.Widget("X", "Button"), ref x); Gui.SameLine(); changed |= Gui.Checkbox(EditorLocalization.Widget("Y", "Button"), ref y); Gui.SameLine(); changed |= Gui.Checkbox(EditorLocalization.Widget("Z", "Button"), ref z);
        if (changed) current = current with { Axes = (x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0) };
        var opacity = current.Opacity; Gui.SetNextItemWidth(UiScale(150));
        if (Gui.InputFloat("Opacity", ref opacity, .1f, .1f, "%.3f") && float.IsFinite(opacity)) current = current with { Opacity = Math.Clamp(opacity, 0, 1) };
        var color = new System.Numerics.Vector3(current.Color.X, current.Color.Y, current.Color.Z);
        Gui.SetNextItemWidth(UiScale(200));
        if (Gui.ColorEdit3("Color", ref color)) current = current with { Color = new(color.X, color.Y, color.Z) };
        if (current != grid.Settings && current.Valid) { grid.ApplySettings(current); if (context == null) pendingGridSettingsSave = true; else context.PendingGridSave = true; }
        Gui.EndPopup();
        SavePendingGridSettings(context);
    }
    private void SavePendingGridSettings(SceneToolbarContext? context = null)
    {
        if (context != null)
        {
            if (context.PendingGridSave && !Gui.IsAnyItemActive() && context.Current() && context.SaveViewSettings != null)
            { context.SaveViewSettings(); context.PendingGridSave = false; }
            return;
        }
        if (!pendingGridSettingsSave || Gui.IsAnyItemActive()) return;
        pendingGridSettingsSave = false; SaveSceneCameraSettings();
    }
}
