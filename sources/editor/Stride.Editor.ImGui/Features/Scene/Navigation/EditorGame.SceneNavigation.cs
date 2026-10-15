using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSceneNavigationControls(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        if (engineViewport is not { } viewport || viewport.NavigationGroups.Count == 0) return;
        if (SceneToolbarIcon("scene-toolbar-navigation", EditorLocalization.Text("Navigation visibility...", "ToolTip"))) Gui.OpenPopup("Navigation meshes");
        if (!Gui.BeginPopup("Navigation meshes")) return;
        Gui.TextUnformatted(EditorLocalization.Text("Show navigation meshes:"));
        if (Gui.Button(EditorLocalization.Widget("All", "Button"))) foreach (var group in viewport.NavigationGroups) viewport.SetNavigationGroupVisible(group.Id, true);
        Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("None", "Button"))) foreach (var group in viewport.NavigationGroups) viewport.SetNavigationGroupVisible(group.Id, false);
        foreach (var group in viewport.NavigationGroups)
        {
            var visible = group.Visible; Gui.PushID(group.Id.ToString());
            var origin = Gui.GetCursorScreenPos();
            Gui.Dummy(UiScale(20, 20));
            Gui.GetWindowDrawList().AddRectFilled(origin, origin + UiScale(20, 20), Gui.GetColorU32(new System.Numerics.Vector4(group.Color.R, group.Color.G, group.Color.B, group.Color.A)));
            Gui.GetWindowDrawList().AddRect(origin, origin + UiScale(20, 20), Gui.GetColorU32(Hexa.NET.ImGui.ImGuiCol.Border));
            Gui.SameLine();
            if (Gui.Checkbox(group.Name, ref visible)) viewport.SetNavigationGroupVisible(group.Id, visible);
            Gui.PopID();
        }
        Gui.EndPopup();
    }
}
