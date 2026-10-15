using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanRenameEntity(Entity entity)
    {
        if (!CanSave || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) || !ReferenceEquals(design.Entity, entity)) return false;
        var node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(entity)[nameof(Entity.Name)];
        return CanEditHierarchyNode(node);
    }
    internal bool CanRenameSelected => Selected is { } entity && CanRenameEntity(entity);
    public void Rename(Entity entity, string name)
    {
        ArgumentNullException.ThrowIfNull(name); EndEdits();
        if (!CanRenameEntity(entity)) throw new InvalidOperationException("This entity name cannot be edited.");
        var before = entity.Name;
        if (before == name) return;
        var node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(entity)[nameof(Entity.Name)];
        ApplyInheritedGraphMutation(() => node.Update(name), $"Rename entity: {before} → {name}");
        LastAction = $"Rename entity: {before} → {name}";
    }
}
