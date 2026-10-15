using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private float sceneToolbarRight;
    private bool sceneToolbarHasItem;

    private void BeginSceneToolbar(float right) { sceneToolbarRight = right; sceneToolbarHasItem = false; }

    private void NextSceneToolbarItem(float width)
    {
        if (sceneToolbarHasItem && Gui.GetItemRectMax().X + Gui.GetStyle().ItemSpacing.X + width <= sceneToolbarRight)
            Gui.SameLine();
        sceneToolbarHasItem = true;
    }

    private bool SceneToolbarIcon(string icon, string tooltip, bool selected = false)
    {
        NextSceneToolbarItem(UiScale(16) + Gui.GetStyle().FramePadding.X * 2);
        Gui.PushStyleColor(ImGuiCol.Button, selected ? Gui.GetStyle().Colors[(int)ImGuiCol.HeaderActive] : new System.Numerics.Vector4(0));
        var clicked = icons!.Button(icon, tooltip, 16);
        Gui.PopStyleColor(); return clicked;
    }

    private void SceneToolbarSeparator()
    {
        NextSceneToolbarItem(UiScale(8));
        Gui.Dummy(new System.Numerics.Vector2(UiScale(8), Gui.GetFrameHeight()));
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        var x = (minimum.X + maximum.X) * 0.5f;
        Gui.GetWindowDrawList().AddLine(new System.Numerics.Vector2(x, minimum.Y + UiScale(3)),
            new System.Numerics.Vector2(x, maximum.Y - UiScale(3)), Gui.GetColorU32(ImGuiCol.Separator));
    }
}
