using Stride.UI.Assets;
using Stride.Assets.Quantum;
using Stride.Core.Assets.Quantum;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal bool CanUngroup(Guid id) => CanSave && Find(id) is Panel panel && panel.VisualParent is { } parent
        && (parent is Panel || parent is ContentControl && panel.VisualChildren.Count <= 1);
    internal IReadOnlyList<Guid> Ungroup(Guid id)
    {
        CommitEdits();
        if (!CanUngroup(id)) throw new InvalidOperationException("The selected UI panel cannot be ungrouped.");
        var panel = (Panel)Require(id); var parent = panel.VisualParent;
        var children = panel.VisualChildren.ToArray();
        var hierarchy = UIAssetPropertyGraph.CloneSubHierarchies(UIHierarchyGraph.Container.NodeContainer,
            UIHierarchyGraph.Container.NodeContainer, Asset, children.Select(child => child.Id), SubHierarchyCloneFlags.None, out _);
        var childDesigns = children.Select(child => Asset.Hierarchy.Parts[child.Id]).ToArray();
        var panelDesign = Asset.Hierarchy.Parts[id];
        Mutate(() =>
        {
            foreach (var child in childDesigns) UIHierarchyGraph.RemovePartFromAsset(child);
            UIHierarchyGraph.RemovePartFromAsset(panelDesign);
            // Original PanelViewModel.Ungroup appends to the actual parent, retaining native IDs/base metadata.
            foreach (var child in children)
                UIHierarchyGraph.AddPartToAsset(hierarchy.Parts, hierarchy.Parts[child.Id], parent, parent.VisualChildren.Count);
        }, "Ungroup " + children.Length + " elements");
        return children.Select(child => child.Id).ToArray();
    }
}
