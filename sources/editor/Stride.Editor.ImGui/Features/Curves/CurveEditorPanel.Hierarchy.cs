using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class CurveEditorPanel
{
    private void DrawCurveHierarchy(CurveBinding binding, CurveHierarchyNode node, CurveHierarchyNode? parent)
    {
        const string rowId = "##Curve";
        var flags = ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (ReferenceEquals(active, node.Track)) flags |= ImGuiTreeNodeFlags.Selected;
        if (node.Children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        var identity = EditorTreeKeyboardAdapter.BeginRow(rowId);
        var open = drawTree(rowId, node.Name, flags);
        curveKeyboardRows.Add(new(binding, node.Track, EditorTreeKeyboardAdapter.EndRow(identity), node, parent, open));
        if (Gui.IsItemClicked() && !Gui.IsItemToggledOpen())
            Run(() => { active = node.Track; ResetSelection(); Fit(0); });
        if (!open || node.Children.Count == 0) return;
        try
        {
            for (var index = 0; index < node.Children.Count; index++)
            {
                Gui.PushID(index);
                try { DrawCurveHierarchy(binding, node.Children[index], node); }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.TreePop(); }
    }
}
