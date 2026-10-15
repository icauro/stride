using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void PlacePreviewToolbarItem(float rowY, float rowHeight, float itemHeight)
        => Gui.SetCursorScreenPos(new NVector2(Gui.GetCursorScreenPos().X, rowY + Math.Max(0, (rowHeight - itemHeight) / 2)));

    private bool DrawPreviewToolbarIcon(string name, float rowY, float rowHeight, float margin = 2, string? tooltip = null)
    {
        var origin = Gui.GetCursorScreenPos();
        Gui.SetCursorScreenPos(new NVector2(origin.X + UiScale(margin), rowY));
        PlacePreviewToolbarItem(rowY, rowHeight, UiScale(16) + Gui.GetStyle().FramePadding.Y * 2);
        var pressed = icons!.Button(name, tooltip, 16);
        Gui.SameLine(0, UiScale(margin));
        return pressed;
    }

    private void DrawPreviewToolbarSeparator(float rowY, float rowHeight)
    {
        Gui.SetCursorScreenPos(new NVector2(Gui.GetCursorScreenPos().X, rowY));
        var origin = Gui.GetCursorScreenPos();
        Gui.Dummy(new NVector2(UiScale(13), rowHeight));
        Gui.GetWindowDrawList().AddRectFilled(origin + new NVector2(UiScale(6), UiScale(2)),
            origin + new NVector2(UiScale(7), Math.Max(UiScale(2), rowHeight - UiScale(2))), Gui.GetColorU32(ImGuiCol.Border));
        Gui.SameLine(0, 0);
    }
}
