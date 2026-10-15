using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal const string HierarchyTransferOperationName = "Move entities to the scene";
    internal bool CanMoveHierarchyRootsTo(EntityHierarchyDocument destination, IEnumerable<Entity> roots)
    {
        var selected = roots.ToArray();
        if (ReferenceEquals(this, destination) || SessionHistory == null || !ReferenceEquals(SessionHistory, destination.SessionHistory)
            || !CanCopyHierarchyRoots(selected) || selected.Any(entity => !CanDeleteEntity(entity))) return false;
        var graphs = new Dictionary<AssetId, AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>>();
        var pendingGraphs = new Stack<AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>>(); pendingGraphs.Push(HierarchyGraph);
        while (pendingGraphs.TryPop(out var baseGraph))
        {
            if (!graphs.TryAdd(baseGraph.Asset.Id, baseGraph)) continue;
            foreach (var inherited in baseGraph.GetBasePartAssets()) pendingGraphs.Push(inherited);
        }
        var pendingBases = new Stack<AssetId>();
        foreach (var entity in selected.SelectMany(Subtree))
            if (Asset.Hierarchy.Parts[entity.Id].Base is { } link)
            {
                if (!graphs.TryGetValue(link.BasePartAsset.Id, out var directGraph) || directGraph.Asset is not EntityHierarchyAssetBase directAsset
                    || !directAsset.Hierarchy.Parts.ContainsKey(link.BasePartId)) return false;
                pendingBases.Push(link.BasePartAsset.Id);
            }
        var visited = new HashSet<AssetId>();
        while (pendingBases.TryPop(out var id))
        {
            if (id == destination.Asset.Id) return false;
            if (!visited.Add(id)) continue;
            if (!graphs.TryGetValue(id, out var inheritedGraph) || inheritedGraph.Asset is not EntityHierarchyAssetBase inheritedAsset) return false;
            foreach (var part in inheritedAsset.Hierarchy.Parts.Values)
                if (part.Base is { } link) pendingBases.Push(link.BasePartAsset.Id);
        }
        return true;
    }
}
