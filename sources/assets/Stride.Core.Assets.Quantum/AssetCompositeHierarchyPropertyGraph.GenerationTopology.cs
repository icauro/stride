using Stride.Core.Reflection;
using Stride.Core.Quantum;

namespace Stride.Core.Assets.Quantum;

public abstract partial class AssetCompositeHierarchyPropertyGraph<TAssetPartDesign, TAssetPart>
    where TAssetPart : class, IIdentifiable
    where TAssetPartDesign : class, IAssetPartDesign<TAssetPart>
{
    // Prepared from actual old/new graphs before their leases change. The journal, not this
    // transient comparison, owns all authored moves and their undo/redo.
    private sealed record GenerationPlacement(Guid Part, Guid BasePart, Guid Instance, Guid OriginParent,
        int OriginIndex, Guid RootOwner, Guid NewBaseParent);
    private readonly Dictionary<AssetId, GenerationPlacement[]> pendingGenerationTopology = [];

    private void PrepareGenerationTopologyDelta(AssetCompositeHierarchyPropertyGraph<TAssetPartDesign, TAssetPart> previous,
        AssetCompositeHierarchyPropertyGraph<TAssetPartDesign, TAssetPart> next)
    {
        var placements = new List<GenerationPlacement>();
        foreach (var current in Asset.Hierarchy.Parts.Values.Where(part => part.Base?.BasePartAsset.Id == next.Id))
        {
            var link = current.Base!;
            if (!previous.Asset.Hierarchy.Parts.TryGetValue(link.BasePartId, out var oldSource)
                || !next.Asset.Hierarchy.Parts.TryGetValue(link.BasePartId, out var newSource)) continue;
            var oldParent = previous.Asset.GetParent(oldSource.Part);
            var newParent = next.Asset.GetParent(newSource.Part);
            var actualParent = Asset.GetParent(current.Part);
            var rootOwner = instancesCommonAncestors.GetValueOrDefault(link.InstanceId);
            var actualParentLink = actualParent != null ? Asset.Hierarchy.Parts[actualParent.Id].Base : null;
            var sameParent = oldParent == null ? (actualParent?.Id ?? Guid.Empty) == rootOwner
                : actualParentLink?.BasePartAsset.Id == next.Id && actualParentLink.InstanceId == link.InstanceId
                    && actualParentLink.BasePartId == oldParent.Id;
            if (!sameParent || HasLocalPlacementOverride(actualParent, current.Part)) continue;

            var actualSiblings = Siblings(Asset, actualParent).Select(part => Asset.Hierarchy.Parts[part.Id])
                .Where(part => part.Base?.BasePartAsset.Id == next.Id && part.Base.InstanceId == link.InstanceId)
                .Select(part => part.Base!.BasePartId).ToArray();
            var oldSiblings = Siblings(previous.Asset, oldParent).Select(part => part.Id).Where(actualSiblings.Contains).ToArray();
            var position = Array.IndexOf(actualSiblings, link.BasePartId);
            var oldPosition = Array.IndexOf(oldSiblings, link.BasePartId);
            if (position < 0 || oldPosition < 0 || Neighbor(actualSiblings, position - 1) != Neighbor(oldSiblings, oldPosition - 1)
                || Neighbor(actualSiblings, position + 1) != Neighbor(oldSiblings, oldPosition + 1)) continue;
            var newSiblings = Siblings(next.Asset, newParent).Select(part => part.Id).Where(actualSiblings.Contains).ToArray();
            if (oldParent?.Id == newParent?.Id && oldSiblings.SequenceEqual(newSiblings)) continue;
            placements.Add(new(current.Part.Id, link.BasePartId, link.InstanceId, actualParent?.Id ?? Guid.Empty,
                Asset.IndexOf(current.Part), rootOwner, newParent?.Id ?? Guid.Empty));
        }
        pendingGenerationTopology[next.Id] = placements.ToArray();
    }

    private bool HasLocalPlacementOverride(TAssetPart? parent, TAssetPart part)
    {
        IEnumerable<IGraphNode> nodes = parent == null
            ? [HierarchyNode[nameof(AssetCompositeHierarchyData<TAssetPartDesign, TAssetPart>.RootParts)].Target!]
            : RetrieveChildPartNodes(parent);
        foreach (var node in nodes.OfType<IAssetObjectNode>())
        {
            if (node.BaseNode is not IAssetObjectNode source) continue;
            foreach (var index in node.Indices ?? [])
            {
                var value = node.Retrieve(index);
                if (value == null || (parent == null ? ((TAssetPart)value).Id : GetIdFromChildPart(value)) != part.Id
                    || !node.TryIndexToId(index, out var id) || !source.HasId(id)) continue;
                return node.GetItemOverride(index).HasFlag(OverrideType.New) || node.GetKeyOverride(index).HasFlag(OverrideType.New);
            }
        }
        return false;
    }

    private static Guid Neighbor(Guid[] parts, int index) => index >= 0 && index < parts.Length ? parts[index] : Guid.Empty;
    private void ValidateGenerationTopologyDelta(AssetId source)
    {
        if (!pendingGenerationTopology.TryGetValue(source, out var placements)) return;
        pendingGenerationTopology[source] = placements.Where(placement => Asset.Hierarchy.Parts.TryGetValue(placement.Part, out var design)
            && (Asset.GetParent(design.Part)?.Id ?? Guid.Empty) == placement.OriginParent
            && Asset.IndexOf(design.Part) == placement.OriginIndex).ToArray();
    }
    private static IEnumerable<TAssetPart> Siblings(AssetCompositeHierarchy<TAssetPartDesign, TAssetPart> asset, TAssetPart? parent)
        => parent == null ? asset.Hierarchy.RootParts : asset.EnumerateChildParts(parent, false);

    private void ApplyGenerationTopologyDelta(AssetCompositeHierarchyPropertyGraph<TAssetPartDesign, TAssetPart> source)
    {
        if (!pendingGenerationTopology.TryGetValue(source.Id, out var prepared)) return;
        var current = prepared.Where(placement => Asset.Hierarchy.Parts.TryGetValue(placement.Part, out var design)
            && design.Base?.BasePartAsset.Id == source.Id && design.Base.InstanceId == placement.Instance
            && design.Base.BasePartId == placement.BasePart
            && (Asset.GetParent(design.Part)?.Id ?? Guid.Empty) == placement.OriginParent).ToArray();
        var previousUpdating = UpdatingPropertyFromBase;
        UpdatingPropertyFromBase = true;
        try
        {
            foreach (var placement in current.OrderBy(placement => SourceTraversalIndex(source.Asset, placement.BasePart)))
            {
                if (!Asset.Hierarchy.Parts.TryGetValue(placement.Part, out var design)) continue;
                TAssetPart? destination;
                if (placement.NewBaseParent == Guid.Empty)
                {
                    destination = placement.RootOwner == Guid.Empty ? null
                        : Asset.Hierarchy.Parts.GetValueOrDefault(placement.RootOwner)?.Part;
                    if (placement.RootOwner != Guid.Empty && destination == null) continue;
                }
                else
                {
                    destination = Asset.Hierarchy.Parts.Values.FirstOrDefault(part => part.Base?.BasePartAsset.Id == source.Id
                        && part.Base.InstanceId == placement.Instance && part.Base.BasePartId == placement.NewBaseParent)?.Part;
                    if (destination == null) continue;
                }
                if (destination != null && (destination.Id == design.Part.Id
                    || Asset.EnumerateChildParts(design.Part, true).Any(part => part.Id == destination.Id))) continue;
                var siblings = Siblings(source.Asset, source.Asset.GetParent(source.Asset.Hierarchy.Parts[placement.BasePart].Part)).ToArray();
                var at = Array.FindIndex(siblings, part => part.Id == placement.BasePart);
                var index = FindGenerationInsertIndex(source.Id, placement.Instance, siblings, at, destination, design.Part);
                if (ReferenceEquals(Asset.GetParent(design.Part), destination) && Asset.IndexOf(design.Part) == index) continue;
                var subtree = new AssetPartCollection<TAssetPartDesign, TAssetPart>();
                foreach (var part in Asset.Hierarchy.Parts.Values) subtree.Add(part);
                RemovePartFromAsset(design);
                AddPartToAsset(subtree, design, destination, index);
            }
            pendingGenerationTopology.Remove(source.Id);
        }
        finally { UpdatingPropertyFromBase = previousUpdating; }
    }

    private int FindGenerationInsertIndex(AssetId source, Guid instance, TAssetPart[] sourceSiblings, int at,
        TAssetPart? parent, TAssetPart moving)
    {
        var children = Siblings(Asset, parent).Where(part => part.Id != moving.Id).ToArray();
        for (var offset = at - 1; offset >= 0; offset--)
        {
            var anchor = Array.FindIndex(children, part => Asset.Hierarchy.Parts[part.Id].Base is { } link
                && link.BasePartAsset.Id == source && link.InstanceId == instance && link.BasePartId == sourceSiblings[offset].Id);
            if (anchor >= 0) return anchor + 1;
        }
        for (var offset = at + 1; offset < sourceSiblings.Length; offset++)
        {
            var anchor = Array.FindIndex(children, part => Asset.Hierarchy.Parts[part.Id].Base is { } link
                && link.BasePartAsset.Id == source && link.InstanceId == instance && link.BasePartId == sourceSiblings[offset].Id);
            if (anchor >= 0) return anchor;
        }
        return children.Length;
    }

    private bool TryGenerationInsertLocation(AssetCompositeHierarchyPropertyGraph<TAssetPartDesign, TAssetPart> source,
        TAssetPartDesign part, TAssetPart? sourceParent, Guid instance, out TAssetPartDesign? destination, out int index)
    {
        destination = null; index = -1;
        if (sourceParent != null)
        {
            destination = Asset.Hierarchy.Parts.Values.FirstOrDefault(candidate => candidate.Base?.BasePartAsset.Id == source.Id
                && candidate.Base.InstanceId == instance && candidate.Base.BasePartId == sourceParent.Id);
            if (destination == null) return false;
        }
        else if (instancesCommonAncestors.TryGetValue(instance, out var rootOwner) && rootOwner != Guid.Empty)
        {
            if (!Asset.Hierarchy.Parts.TryGetValue(rootOwner, out destination)) return false;
        }
        var siblings = Siblings(source.Asset, sourceParent).ToArray();
        var at = Array.FindIndex(siblings, candidate => candidate.Id == part.Part.Id);
        index = FindGenerationInsertIndex(source.Id, instance, siblings, at, destination?.Part, part.Part);
        return true;
    }

    private static int SourceTraversalIndex(AssetCompositeHierarchy<TAssetPartDesign, TAssetPart> source, Guid id)
    {
        var index = 0;
        foreach (var root in source.Hierarchy.RootParts)
        {
            if (root.Id == id) return index;
            index++;
            foreach (var child in source.EnumerateChildParts(root, true)) { if (child.Id == id) return index; index++; }
        }
        return int.MaxValue;
    }
}
