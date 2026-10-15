using Hexa.NET.ImGui;
using System.Numerics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Shared tree-row presentation; callers retain stable IDs, data and commands.</summary>
internal sealed class EditorTreeView
{
    private int visibleRow;
    private float left, right;
    internal Func<string, Vector2>? MeasureText { get; set; }
    internal Action<string, Vector2, Vector2, Vector2>? PaintText { get; set; }
    internal Func<string, bool, bool>? SelectNativeText { get; set; }

    internal void Begin()
    {
        visibleRow = 0;
        left = Gui.GetCursorScreenPos().X;
        right = left + Gui.GetContentRegionAvail().X;
    }

    private void DrawBand(bool framed, float? textHeight = null)
    {
        var alternate = (visibleRow++ & 1) != 0;
        if (!alternate) return;
        var style = Gui.GetStyle();
        var top = Gui.GetCursorScreenPos().Y;
        var height = (textHeight ?? Gui.GetTextLineHeight()) + (framed ? style.FramePadding.Y * 2 : 0);
        var background = style.Colors[(int)ImGuiCol.WindowBg];
        var stripe = Vector4.Lerp(background, style.Colors[(int)ImGuiCol.Text], .045f);
        Gui.GetWindowDrawList().AddRectFilled(new Vector2(left, top),
            new Vector2(right, top + height + style.ItemSpacing.Y), Gui.GetColorU32(stripe));
    }

    internal void SkipRow() => visibleRow++;

    internal bool Node(string id, ImGuiTreeNodeFlags flags)
    {
        if (MeasureText != null && PaintText != null)
            return NativeNode(id, VisibleLabel(id), flags, true, out _);
        DrawBand((flags & ImGuiTreeNodeFlags.FramePadding) != 0);
        return Gui.TreeNodeEx(id, flags | ImGuiTreeNodeFlags.DrawLinesToNodes);
    }
    internal bool NativeNode(string id, string caption, ImGuiTreeNodeFlags flags, bool forceSubmission, out bool virtualized, bool paint = true, float minimumHeight = 0)
    {
        var textSize = MeasureText?.Invoke(caption) ?? Gui.CalcTextSize(caption);
        using var padding = EditorNativeTreeGeometry.RowPadding(Math.Max(textSize.Y, minimumHeight), ref flags);
        flags |= ImGuiTreeNodeFlags.DrawLinesToNodes;
        virtualized = EditorTreeVirtualizationAdapter.TrySkipRow(id, flags, forceSubmission, out var expanded);
        if (virtualized) { SkipRow(); return expanded; }
        DrawBand((flags & (ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.Framed)) != 0);
        expanded = ImGuiP.TreeNodeBehavior(Gui.GetID(id), flags, "");
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        var origin = minimum + new Vector2(Gui.GetTreeNodeToLabelSpacing(), Math.Max(0, (maximum.Y - minimum.Y - textSize.Y) / 2));
        if (paint)
        {
            if (PaintText != null) PaintText(caption, origin, minimum, maximum);
            else Gui.GetWindowDrawList().AddText(origin, Gui.GetColorU32(ImGuiCol.Text), caption);
        }
        return expanded;
    }

    internal bool Selectable(string label, bool selected)
    {
        DrawBand(false, MeasureText?.Invoke(VisibleLabel(label)).Y);
        if (SelectNativeText != null) return SelectNativeText(label, selected);
        return Gui.Selectable(label, selected);
    }
    private static string VisibleLabel(string label)
    {
        var suffix = label.IndexOf("##", StringComparison.Ordinal);
        return suffix < 0 ? label : label[..suffix];
    }
}
