using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawHierarchyEmptyAreaDrop()
    {
        // Native VirtualizingTreePanel binds its drop context to ActiveRoot, falling back to HierarchyRoot.
        var active = document;
        if (active == null || IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(active)) return;
        var available = Gui.GetContentRegionAvail();
        if (available.X <= 0 || available.Y <= 0) return;
        Gui.InvisibleButton("##HierarchyEmptyArea", available);
        var previousMinimum = hierarchyRowMinimum;
        var previousMaximum = hierarchyRowMaximum;
        hierarchyRowMinimum = Gui.GetItemRectMin();
        hierarchyRowMaximum = Gui.GetItemRectMax();
        try
        {
            DrawEntityDragDrop(null, active, emptyArea: true);
            if (active is SceneDocument scene && PresentedHierarchyDocument is SceneDocument presented)
                DrawSceneRootDragDrop(scene, presented, emptyArea: true);
        }
        finally { hierarchyRowMinimum = previousMinimum; hierarchyRowMaximum = previousMaximum; }
    }
}
