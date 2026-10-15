using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal static IEnumerable<Type> RegisteredComponentTypes => typeof(EntityComponent).GetInheritedInstantiableTypes()
        .Where(type => type != typeof(TransformComponent) && Attribute.GetCustomAttribute(type, typeof(NonInstantiableAttribute)) == null);

    internal bool CanChangeComponents(Entity entity) => CanSave && Asset.Archetype == null
        && Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) && ReferenceEquals(design.Entity, entity) && design.Base == null;

    private bool CanMutateComponentCollection(Entity entity)
    {
        if (!CanSave || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) || !ReferenceEquals(design.Entity, entity)) return false;
        if (design.Base != null && (SessionHistory == null
            || HierarchyGraph.Container.TryGetGraph(design.Base.BasePartAsset.Id) is not Stride.Assets.Quantum.EntityHierarchyPropertyGraph source
            || !source.Asset.Hierarchy.Parts.ContainsKey(design.Base.BasePartId))) return false;
        EnsureHierarchyGraph();
        var member = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(entity)[nameof(Entity.Components)];
        var collection = ComponentCollection(entity);
        return CanEditHierarchyNode(collection) && !PropertyClipboardRules.IsReadOnly(member);
    }
    internal bool CanAddComponents(Entity entity) => CanMutateComponentCollection(entity) && NativePropertyClipboard.HasAdd(ComponentCollection(entity));
    internal bool CanAddComponent(Entity entity, Type type) => CanAddComponents(entity) && RegisteredComponentTypes.Contains(type)
        && (EntityComponentAttributes.Get(type).AllowMultipleComponents || entity.Components.All(component => component.GetType() != type));

    internal EntityComponent AddComponent(Entity entity, Type type)
    {
        EndEdits();
        if (!CanAddComponent(entity, type)) throw new InvalidOperationException("This component cannot be added to this entity.");
        EntityComponent? component = null;
        ApplyInheritedGraphMutation(() =>
        {
            var graph = HierarchyGraph;
            void MemberChanging(object? _, MemberNodeChangeEventArgs change)
            {
                if (!CanEditHierarchyNode(change.Member) || !AssetMetadataDocument.CanAssignAuthoringNode(change.Member))
                    throw new InvalidOperationException("The component factory cannot edit this property.");
            }
            void ItemChanging(object? _, ItemChangeEventArgs change)
            {
                var allowed = change.Collection is IAssetObjectNode collection &&
                    (change.ChangeType == ContentChangeType.CollectionAdd
                        ? CanEditHierarchyNode(collection) && NativePropertyClipboard.HasAdd(collection)
                            && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(collection.Type)?.ReadOnly != true
                        : CanEditHierarchyItem(collection, change.Index));
                if (!allowed) throw new InvalidOperationException("The component factory cannot edit this collection item.");
            }
            graph.Changing += MemberChanging; graph.ItemChanging += ItemChanging;
            try
            {
                component = ObjectFactoryRegistry.NewInstance(type) as EntityComponent
                    ?? throw new InvalidOperationException("The component could not be created.");
                if (component.GetType() != type || component.Entity != null) throw new InvalidOperationException("The component factory returned an incompatible or attached component.");
                if (!CanAddComponent(entity, type) || component.Entity != null)
                    throw new InvalidOperationException("The component destination changed after factory creation.");
                if (component.Id == Guid.Empty || Asset.Hierarchy.Parts.Values.Any(part => part.Entity.Components.Any(existing => existing.Id == component.Id)))
                    component.Id = Guid.NewGuid();
                AssetCollectionItemIdHelper.GenerateMissingItemIds(component);
                ComponentCollection(entity).Add(component);
            }
            finally { graph.Changing -= MemberChanging; graph.ItemChanging -= ItemChanging; }
        }, "Add component: " + DisplayAttribute.GetDisplayName(type));
        LastAction = "Add component: " + DisplayAttribute.GetDisplayName(type);
        return component!;
    }

    internal bool CanRemoveComponent(EntityComponent component) => component is not TransformComponent && component.Entity is { } entity
        && CanMutateComponentCollection(entity) && entity.Components.Contains(component)
        && NativePropertyClipboard.HasRemove(ComponentCollection(entity))
        && CanEditHierarchyItem(ComponentCollection(entity), new NodeIndex(entity.Components.IndexOf(component)));

    internal void RemoveComponent(EntityComponent component)
    {
        EndEdits();
        if (!CanRemoveComponent(component)) throw new InvalidOperationException("This component cannot be removed.");
        var entity = component.Entity;
        ApplyInheritedGraphMutation(() =>
        {
            if (!CanRemoveComponent(component) || !ReferenceEquals(component.Entity, entity))
                throw new InvalidOperationException("The component destination changed.");
            var graph = HierarchyGraph;
            void MemberChanging(object? _, MemberNodeChangeEventArgs change)
            {
                if (!CanEditHierarchyNode(change.Member) || !AssetMetadataDocument.CanAssignAuthoringNode(change.Member))
                    throw new InvalidOperationException("The removed component has a protected reference.");
            }
            void ItemChanging(object? _, ItemChangeEventArgs change)
            {
                if (change.Collection is not IAssetObjectNode collection || !CanEditHierarchyItem(collection, change.Index))
                    throw new InvalidOperationException("The removed component has a protected collection reference.");
            }
            graph.Changing += MemberChanging; graph.ItemChanging += ItemChanging;
            try
            {
                graph.ClearReferencesToObjects([component.Id]);
                if (!CanRemoveComponent(component) || !ReferenceEquals(component.Entity, entity))
                    throw new InvalidOperationException("The component destination changed during reference cleanup.");
                ComponentCollection(entity).Remove(component, new NodeIndex(entity.Components.IndexOf(component)));
            }
            finally { graph.Changing -= MemberChanging; graph.ItemChanging -= ItemChanging; }
        }, "Remove component: " + DisplayAttribute.GetDisplayName(component.GetType()));
        LastAction = "Remove component: " + DisplayAttribute.GetDisplayName(component.GetType());
    }

    private IAssetObjectNode ComponentCollection(Entity entity) => (IAssetObjectNode)hierarchyGraph!.Container.NodeContainer
        .GetOrCreateNode(entity)[nameof(Entity.Components)].Target!;

    internal HashSet<IIdentifiable> GetComponentExternalReferences(EntityComponent component)
    {
        EnsureHierarchyGraph();
        var node = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(component);
        var references = ExternalReferenceCollector.GetExternalReferences(hierarchyGraph.Definition, node);
        references.Add(component.Entity);
        return references;
    }

    private void InvalidateComponentStructure()
    {
        DisposePropertyPresenters();
        componentFields.Clear(); entityReferenceFields.Clear(); synchronizationFields.Clear();
        collectionItemFields.Clear(); collectionItems.Clear(); collectionTypes.Clear(); StructureVersion++;
    }
}
