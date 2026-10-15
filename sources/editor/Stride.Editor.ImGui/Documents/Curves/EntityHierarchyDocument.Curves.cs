using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal CurveBinding? BindCurve(IMemberNode member, string title)
    {
        var owner = historyScope.AuthoringState;
        if (owner == null || !CurveBinding.IsSupported(member.Retrieve())) return null;
        return CurveBinding.Create(new(owner, Asset, HierarchyGraph, CanEditHierarchyNode, CanEditHierarchyItem, ApplyInheritedGraphMutation), member, title);
    }
}
