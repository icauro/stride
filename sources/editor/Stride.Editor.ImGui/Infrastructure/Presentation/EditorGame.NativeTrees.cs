using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawNativeTreeText(string text, Vector2 origin, Vector2 minimum, Vector2 maximum, uint? color = null)
    {
        if (CanDrawNativeWidgetText) PaintNativeWidget(NativeWidgetLayout(text, color: color), origin, minimum, maximum);
        else
        {
            var draw = Gui.GetWindowDrawList(); draw.PushClipRect(minimum, maximum, true);
            try { draw.AddText(origin, color ?? Gui.GetColorU32(ImGuiCol.Text), text); }
            finally { draw.PopClipRect(); }
        }
        LogNativeWidgetText(text, origin);
    }
    private bool DrawNativeTreeNode(string label, ImGuiTreeNodeFlags flags, bool forceSubmission, out bool virtualized, string? caption = null, bool paint = true)
    {
        if (!CanDrawNativeWidgetText)
        {
            virtualized = EditorTreeVirtualizationAdapter.TrySkipRow(label, flags, forceSubmission, out var open);
            return virtualized ? open : Gui.TreeNodeEx(label, flags);
        }
        var text = caption ?? NativeVisibleWidgetLabel(label); var size = MeasureNativeLabel(text);
        using var padding = EditorNativeTreeGeometry.RowPadding(size.Y, ref flags);
        virtualized = EditorTreeVirtualizationAdapter.TrySkipRow(label, flags, forceSubmission, out var expanded);
        if (virtualized) return expanded;
        // Native ID/state/arrow/line/depth/nav handling owns the row; its separate label uses one actual layout.
        expanded = ImGuiP.TreeNodeBehavior(Gui.GetID(label), flags, "");
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        var origin = minimum + new Vector2(Gui.GetTreeNodeToLabelSpacing(), Math.Max(0, (maximum.Y - minimum.Y - size.Y) / 2));
        if (paint) DrawNativeTreeText(text, origin, Vector2.Max(origin, minimum), maximum);
        if (!paint) LogNativeWidgetText(text, origin);
        return expanded;
    }
}
