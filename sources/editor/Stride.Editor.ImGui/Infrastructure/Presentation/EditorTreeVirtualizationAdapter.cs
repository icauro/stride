using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal static class EditorTreeVirtualizationAdapter
{
    internal static bool TrySkipRow(string label, ImGuiTreeNodeFlags flags, bool forceSubmission, out bool expanded)
    {
        expanded = false;
        var height = Gui.GetTextLineHeight() + ((flags & ImGuiTreeNodeFlags.FramePadding) != 0 ? Gui.GetStyle().FramePadding.Y * 2 : 0);
        var size = new Vector2(Math.Max(1, Gui.GetContentRegionAvail().X), height);
        var id = Gui.GetID(label);
        var context = Gui.GetCurrentContext();
        if (forceSubmission || Gui.IsRectVisible(size) || context.ActiveId == id || context.NavId == id
            || context.NavMoveSubmitted || context.NavMoveScoringItems) return false;
        // Reuse the real native tree open-state rules, including pending reveal/
        // expand requests; no parallel open-state model or flattened authored tree.
        expanded = ImGuiP.TreeNodeUpdateNextOpen(id, flags);
        Gui.Dummy(size);
        if (expanded && (flags & ImGuiTreeNodeFlags.NoTreePushOnOpen) == 0) ImGuiP.TreePushOverrideID(id);
        return true;
    }
}
