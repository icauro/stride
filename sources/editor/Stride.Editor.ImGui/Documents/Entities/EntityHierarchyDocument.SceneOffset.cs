using Stride.Assets.Entities;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal IMemberNode SceneOffsetNode => Asset is SceneAsset
        ? HierarchyGraph.RootNode[nameof(SceneAsset.Offset)]
        : throw new InvalidOperationException("This hierarchy is not a scene.");

    internal bool CanEditSceneOffset(IMemberNode node) => Asset is SceneAsset && SessionHistory != null
        && ReferenceEquals(SceneOffsetNode, node) && CanEditHierarchyNode(node)
        && AssetMetadataDocument.CanAssignAuthoringNode(node);

    internal void SetSceneOffsetCoordinate(IMemberNode node, int axis, float value)
    {
        if ((uint)axis >= 3 || !float.IsFinite(value)) throw new ArgumentException("The scene offset must be finite.");
        if (!CanEditSceneOffset(node)) throw new InvalidOperationException("This scene offset is read-only or sealed.");
        var current = (Vector3)node.Retrieve();
        if (current[axis] == value) return;
        var next = current; next[axis] = value;
        SetSceneOffset(node, next);
    }
    internal void SetSceneOffset(IMemberNode node, Vector3 next)
    {
        if (!float.IsFinite(next.X) || !float.IsFinite(next.Y) || !float.IsFinite(next.Z)) throw new ArgumentException("The scene offset must be finite.");
        if (!CanEditSceneOffset(node)) throw new InvalidOperationException("This scene offset is read-only or sealed.");
        if (Equals(node.Retrieve(), next)) return;
        ApplyInheritedGraphMutation(() =>
        {
            if (!CanEditSceneOffset(node)) throw new InvalidOperationException("The scene offset owner changed.");
            node.Update(next);
        }, "Edit Offset");
    }
}
