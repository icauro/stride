using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private readonly Dictionary<IAssetNode, List<NodeAccessor[]>> hierarchyAuthoringPaths = [];
    private long authoringPathsStructureVersion = -1;
    private AssetPropertyGraph? authoringPathsGraph;

    private void InvalidateHierarchyAuthoringPaths() => authoringPathsGraph = null;

    internal bool CanEditHierarchyNode(IGraphNode node)
        => CanEditHierarchyNode(node, null);

    private bool CanEditHierarchyNode(IGraphNode node, IMemberNode? collectionMutationMember)
    {
        if (!CanSave || UnknownAssetData.IsProxyNode(node) || node is not IAssetNode asset
            || SessionHistory != null && (historyScope.AuthoringState is not { IsPublished: true } state
                || !ReferenceEquals(SessionHistory.Registry.Find(Asset.Id), state))) return false;
        var graph = HierarchyGraph;
        if (!ReferenceEquals(authoringPathsGraph, graph) || authoringPathsStructureVersion != StructureVersion)
        {
            hierarchyAuthoringPaths.Clear();
            var visitor = graph.CreateReconcilerVisitor();
            visitor.Visiting += (visited, path) =>
            {
                if (visited is not IAssetNode actual) return;
                var ancestors = new List<NodeAccessor>();
                for (var current = path; current != null; current = current.GetParent()) ancestors.Add(current.GetAccessor());
                if (!hierarchyAuthoringPaths.TryGetValue(actual, out var paths)) hierarchyAuthoringPaths.Add(actual, paths = []);
                paths.Add(ancestors.ToArray());
            };
            visitor.Visit(graph.RootNode);
            authoringPathsGraph = graph; authoringPathsStructureVersion = StructureVersion;
        }
        // A shared target reachable through a sealed path remains sealed through its other aliases.
        return hierarchyAuthoringPaths.TryGetValue(asset, out var paths)
            && paths.All(path => path.All(accessor => !UnknownAssetData.IsProxyNode(accessor.Node) && (accessor.Node is not IAssetNode ancestor
                || !graph.IsInheritedNodeSealed(ancestor, accessor.Index))
                && !propertyPresenters.Any(presentation => presentation.IsAccessorReadOnly(accessor.Node, accessor.Index, collectionMutationMember))));
    }

    internal bool CanEditHierarchyItem(IAssetObjectNode collection, NodeIndex index)
        => CanEditHierarchyNode(collection) && (collection.TryIndexToId(index, out _)
            || collection.BaseNode == null && (collection.Indices ?? []).Contains(index))
        && !HierarchyGraph.IsInheritedNodeSealed(collection, index)
        && !propertyPresenters.Any(presentation => presentation.IsAccessorReadOnly(collection, index));

    internal bool CanEditHierarchyField(ComponentField field)
        => field.Component.Entity is { } entity
        && Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) && ReferenceEquals(design.Entity, entity)
        && entity.Components.Contains(field.Component) && IsKnownComponentField(field)
        && CanEditHierarchyNode(field.Node)
        && (design.Base == null || SessionHistory != null
            && HierarchyGraph.Container.TryGetGraph(design.Base.BasePartAsset.Id) is Stride.Assets.Quantum.EntityHierarchyPropertyGraph source
            && source.Asset.Hierarchy.Parts.ContainsKey(design.Base.BasePartId));

    internal bool CanResetHierarchyNode(IGraphNode node)
        => CanEditHierarchyNode(node) && node is IAssetNode { BaseNode: not null };
    internal bool CanResetHierarchyItem(IAssetObjectNode collection, NodeIndex index)
        => CanEditHierarchyItem(collection, index) && collection.BaseNode is IAssetObjectNode source
        && collection.TryIndexToId(index, out var id) && source.HasId(id);
    internal void ResetHierarchyNode(IGraphNode node)
    {
        EndEdits();
        if (!CanResetHierarchyNode(node)) throw new InvalidOperationException("This property has no editable base value.");
        ApplyInheritedGraphMutation(((IAssetNode)node).ResetOverrideRecursively, "Reset to base value");
    }
    internal void ResetHierarchyItem(IAssetObjectNode collection, NodeIndex index)
    {
        EndEdits();
        if (!CanResetHierarchyItem(collection, index)) throw new InvalidOperationException("This item has no editable base value.");
        ApplyInheritedGraphMutation(() => collection.ResetOverrideRecursively(index), "Reset to base value");
    }
}
