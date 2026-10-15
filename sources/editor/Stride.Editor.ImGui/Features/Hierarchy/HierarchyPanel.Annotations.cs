using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class HierarchyPanel
{
    internal void DrawSceneUnloadedAnnotation(string name, uint nameWeight, string text)
    {
        if (RowWasVirtualized) return;
        var nameSize = host.MeasureTreeText(name, nameWeight);
        var textSize = host.MeasureTreeText(text);
        var start = State.hierarchyRowMinimum + new Vector2(Gui.GetTreeNodeToLabelSpacing() + Gui.GetTextLineHeight()
            + Gui.GetStyle().ItemInnerSpacing.X + nameSize.X + 4 * Gui.GetStyle().FontScaleDpi,
            Math.Max(0, (State.hierarchyRowMaximum.Y - State.hierarchyRowMinimum.Y - textSize.Y) / 2));
        var maximum = new Vector2(State.hierarchyRowMaximum.X - ActionWidth, State.hierarchyRowMaximum.Y);
        if (maximum.X > start.X) host.DrawTreeText(text, start, start, maximum, 0xff808080u);
    }
    internal void DrawAnnotations(string name, string? link, string? prefab)
    {
        var size = Gui.GetTextLineHeight();
        var nameSize = host.MeasureTreeText(name);
        var start = State.hierarchyRowMinimum + new Vector2(Gui.GetTreeNodeToLabelSpacing() + size + Gui.GetStyle().ItemInnerSpacing.X,
            Math.Max(0, (State.hierarchyRowMaximum.Y - State.hierarchyRowMinimum.Y - nameSize.Y) / 2));
        start.X += nameSize.X;
        var right = State.hierarchyRowMaximum.X - ActionWidth;
        if (start.X >= right) return;
        var draw = Gui.GetWindowDrawList();
        draw.PushClipRect(start, new Vector2(right, State.hierarchyRowMaximum.Y), true);
        var (linkColor, prefabColor) = host.Theme switch
        {
            EditorTheme.LightSteelBlue => (0x8ABB2Eu, 0x7198D2u),
            EditorTheme.Divided => (0xB0D3FFu, 0x7198D2u),
            _ => (0xB0D3FFu, 0xB0FFD3u),
        };
        static uint Color(uint rgb) => Gui.GetColorU32(new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1));
        if (link != null) { host.DrawTreeText(link, start, start, new(right, State.hierarchyRowMaximum.Y), Color(linkColor)); start.X += host.MeasureTreeText(link).X; }
        if (prefab != null) host.DrawTreeText(prefab, start, start, new(right, State.hierarchyRowMaximum.Y), Color(prefabColor));
        draw.PopClipRect();
    }
}
