using Stride.Core.Assets;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanAttachAssetComponent(Entity entity, Type type)
    {
        if (!CanSave || SessionHistory == null || !ReferenceEquals(SessionHistory.Registry.Find(Asset.Id)?.Document, this)
            || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) || !ReferenceEquals(design.Entity, entity)
            || type == typeof(TransformComponent) || !RegisteredComponentTypes.Contains(type)) return false;
        EnsureHierarchyGraph();
        var collection = ComponentCollection(entity);
        if (!CanEditHierarchyNode(collection)) return false;
        if (!EntityComponentAttributes.Get(type).AllowMultipleComponents)
            for (var index = 0; index < entity.Components.Count; index++)
                if (entity.Components[index].GetType() == type) return CanEditHierarchyItem(collection, new NodeIndex(index));
        return true;
    }

    internal void AttachAssetComponents(Entity entity, EntityComponent[] components, IDisposable[]? resources = null)
    {
        var transferred = false;
        try
        {
            EndEdits(); EnsureHierarchyGraph();
            if (components.Length == 0 || components.Any(component => component.Entity != null || !CanAttachAssetComponent(entity, component.GetType())))
                throw new InvalidOperationException("The assets cannot be added here.");
            foreach (var component in components)
            {
                if (component.Id == Guid.Empty || Asset.Hierarchy.Parts.Values.Any(part => part.Entity.Components.Any(existing => existing.Id == component.Id)))
                    component.Id = Guid.NewGuid();
                AssetCollectionItemIdHelper.GenerateMissingItemIds(component);
            }
            void Attach()
            {
                var collection = ComponentCollection(entity);
                foreach (var component in components)
                {
                    if (!CanAttachAssetComponent(entity, component.GetType())) throw new InvalidOperationException("The component destination changed.");
                    var replaceIndex = -1;
                    if (!EntityComponentAttributes.Get(component.GetType()).AllowMultipleComponents)
                        for (var index = 0; index < entity.Components.Count; index++)
                            if (entity.Components[index].GetType() == component.GetType()) { replaceIndex = index; break; }
                    // Original EntityViewModel property-grid drop updates the actual
                    // same-type slot; multi-instance components append instead.
                    if (replaceIndex < 0) collection.Add(component);
                    else collection.Update(component, new NodeIndex(replaceIndex));
                }
            }
            if (resources == null) ApplyInheritedGraphMutation(Attach, "Add components from assets");
            else { transferred = true; ApplyInheritedGraphMutation(Attach, "Add components from assets", resources); }
        }
        finally { if (!transferred && resources != null) foreach (var resource in resources) resource.Dispose(); }
    }
}
