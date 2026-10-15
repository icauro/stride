using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class HierarchyPanel
{
    internal bool RowWasVirtualized { get; private set; }
    internal bool RowOpen { get; private set; }
    internal float ActionWidth => 2 * (Gui.GetTextLineHeight() + 4 * Gui.GetStyle().FontScaleDpi) + Gui.GetStyle().ItemSpacing.X;
    internal void ApplyHierarchyExpansion()
    {
        var id = Gui.GetID("##HierarchyExpansion");
        if (State.hierarchyExpandAll is { } expand && State.hierarchyExpansionApplied.GetValueOrDefault(id) != State.hierarchyExpansionRevision)
        {
            Gui.SetNextItemOpen(expand, ImGuiCond.Always);
            State.hierarchyExpansionApplied[id] = State.hierarchyExpansionRevision;
        }
    }

    internal bool DrawRow(string name, string icon, ImGuiTreeNodeFlags flags, bool hasActions = true, bool forceSubmission = false, uint? textColor = null, uint textWeight = 400)
    {
        ApplyHierarchyExpansion();
        flags |= ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.FramePadding;
        var textSize = host.MeasureTreeText(name, textWeight);
        using var padding = EditorNativeTreeGeometry.RowPadding(textSize.Y, ref flags);
        var keyboardIdentity = EditorTreeKeyboardAdapter.BeginRow("##Row");
        RowWasVirtualized = EditorTreeVirtualizationAdapter.TrySkipRow("##Row", flags, forceSubmission, out var open);
        if (RowWasVirtualized) { State.hierarchyTree.SkipRow(); RememberKeyboardRow(keyboardIdentity, flags, open); RowOpen = open; return open; }
        open = State.hierarchyTree.Node("##Row", flags);
        RowOpen = open;
        RememberKeyboardRow(keyboardIdentity, flags, open);
        State.hierarchyRowMinimum = Gui.GetItemRectMin(); State.hierarchyRowMaximum = Gui.GetItemRectMax();
        var size = Gui.GetTextLineHeight();
        var label = State.hierarchyRowMinimum + new Vector2(Gui.GetTreeNodeToLabelSpacing(), Math.Max(0, (State.hierarchyRowMaximum.Y - State.hierarchyRowMinimum.Y - size) / 2));
        var right = hasActions ? State.hierarchyRowMaximum.X - ActionWidth : State.hierarchyRowMaximum.X;
        var draw = Gui.GetWindowDrawList();
        draw.PushClipRect(new(label.X, State.hierarchyRowMinimum.Y), new Vector2(Math.Max(label.X, right), State.hierarchyRowMaximum.Y), true);
        draw.AddImage(host.Icons.Get(icon), label, label + new Vector2(size));
        var textOrigin = new Vector2(label.X + size + Gui.GetStyle().ItemInnerSpacing.X,
            State.hierarchyRowMinimum.Y + Math.Max(0, (State.hierarchyRowMaximum.Y - State.hierarchyRowMinimum.Y - textSize.Y) / 2));
        host.DrawTreeText(name, textOrigin, new(label.X, State.hierarchyRowMinimum.Y), new(Math.Max(label.X, right), State.hierarchyRowMaximum.Y), textColor, textWeight);
        draw.PopClipRect();
        return open;
    }

}
