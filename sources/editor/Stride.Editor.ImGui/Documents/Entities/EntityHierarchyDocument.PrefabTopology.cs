using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    // Owned bases can have derived graphs and sealed/item metadata too.
    // Every canonical topology edit needs the same native graph journal.
    private bool RequiresHierarchyJournal => SessionHistory != null;

    private bool CanAuthorHierarchyChildren(Entity? parent)
    {
        if (!CanSave || parent != null && (!Asset.Hierarchy.Parts.TryGetValue(parent.Id, out var part) || !ReferenceEquals(part.Entity, parent))) return false;
        if (SessionHistory == null) return Asset.Archetype == null && (parent == null || Asset.Hierarchy.Parts[parent.Id].Base == null);
        var graph = HierarchyGraph;
        if (parent == null)
            return CanEditHierarchyNode(graph.RootNode[nameof(EntityHierarchyAssetBase.Hierarchy)].Target![nameof(AssetCompositeHierarchyData<EntityDesign, Entity>.RootParts)].Target!);
        var children = graph.Container.NodeContainer.GetOrCreateNode(parent.Transform)[nameof(TransformComponent.Children)];
        // Native hierarchy insertion mutates this collection; it does not assign
        // the getter-only Children property. Keep seals, proxies, ancestor and
        // explicitly assigned provider read-only policies unchanged.
        return CanEditHierarchyNode(children.Target!, children);
    }

    private bool CanAuthorHierarchyPart(Entity entity)
    {
        if (!CanSave || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)
            || !CanAuthorHierarchyChildren(entity.Transform.Parent?.Entity)) return false;
        if (SessionHistory == null) return !HasInheritedHierarchy && Asset.Archetype == null && part.Base == null;
        return Subtree(entity).All(child => CanEditHierarchyNode(HierarchyGraph.Container.NodeContainer.GetOrCreateNode(Asset.Hierarchy.Parts[child.Id])));
    }

    private void ApplyHierarchyTransform(Entity entity, TransformState state)
    {
        var node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(entity.Transform);
        node[nameof(TransformComponent.Position)].Update(state.Position);
        node[nameof(TransformComponent.Rotation)].Update(state.Rotation);
        node[nameof(TransformComponent.Scale)].Update(state.Scale);
    }

    private bool CanAuthorHierarchyTransform(Entity entity)
    {
        var node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(entity.Transform);
        return CanEditHierarchyNode(node[nameof(TransformComponent.Position)])
            && CanEditHierarchyNode(node[nameof(TransformComponent.Rotation)])
            && CanEditHierarchyNode(node[nameof(TransformComponent.Scale)]);
    }

    internal bool CanBreakPrefabLink(Entity entity) => SessionHistory != null
        && Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) && ReferenceEquals(part.Entity, entity) && part.Base != null
        && CanEditHierarchyNode(HierarchyGraph.Container.NodeContainer.GetOrCreateNode(part)[nameof(EntityDesign.Base)]);

    internal bool CanBreakPrefabLinks => SelectedCount > 0 && SessionHistory != null && SelectedIds.All(id =>
        Asset.Hierarchy.Parts.TryGetValue(id, out var design) && (design.Base == null || CanBreakPrefabLink(design.Entity)))
        && SelectedIds.Any(id => Asset.Hierarchy.Parts[id].Base != null);

    internal void BreakSelectedPrefabLinks()
    {
        EndEdits();
        if (!CanBreakPrefabLinks) throw new InvalidOperationException("The selected prefab links cannot be changed.");
        // Native BreakBasePartLinks ignores unlinked selected parts; they require no authoring permission.
        var parts = SelectedIds.Select(id => Asset.Hierarchy.Parts[id]).Where(part => part.Base != null).ToArray();
        ApplyInheritedGraphMutation(() => HierarchyGraph.BreakBasePartLinks(parts), "Break link to prefab");
    }
}
