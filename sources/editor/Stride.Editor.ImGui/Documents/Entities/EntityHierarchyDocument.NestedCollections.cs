using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private long nestedHierarchyCollectionVersion = -1;
    private readonly Dictionary<(IAssetObjectNode Node, string Key), HierarchyCollectionContext> nestedHierarchyCollections = [];
    private readonly Dictionary<(IAssetObjectNode Node, NodeIndex Index, ComponentField Origin), IReadOnlyList<ComponentField>> nestedHierarchyItemFields = [];
    private void RefreshNestedHierarchyCollectionCache()
    {
        if (nestedHierarchyCollectionVersion == StructureVersion) return;
        nestedHierarchyCollectionVersion = StructureVersion; nestedHierarchyCollections.Clear(); nestedHierarchyItemFields.Clear();
    }
    internal HierarchyCollectionContext? GetNestedHierarchyCollection(IHierarchyCollectionValue item)
    {
        RefreshNestedHierarchyCollectionCache();
        if (item is CollectionItem legacy && !legacy.Collection.HasId(legacy.Id)
            || item is HierarchyCollectionItem actual && actual.Id is { } currentId && !actual.Collection.HasId(currentId)) return null;
        var context = item is HierarchyCollectionItem nested ? nested.Context
            : new HierarchyCollectionContext(item.Field, item.Collection, null, StructureVersion);
        if (!IsKnownHierarchyCollection(context) || !HasHierarchyCollectionIndex(item.Collection, item.Index) || item.Value == null
            || HierarchyGraph.Definition.IsTargetItemObjectReference(item.Collection, item.Index, item.Value)
            || item.Collection.ItemReferences == null || item.Collection.IndexedTarget(item.Index) is not IAssetObjectNode { IsEnumerable: true } node) return null;
        var depth = 0;
        for (var ancestor = context; ancestor != null; ancestor = ancestor.Parent?.Context)
            if (++depth > 16 || ReferenceEquals(ancestor.Node, node)) return null;
        var parent = item as HierarchyCollectionItem ?? new(context, item.Index, item.Collection.TryIndexToId(item.Index, out var id) && id != ItemId.Empty ? id : null);
        var key = (node, parent.IdentityKey);
        if (!nestedHierarchyCollections.TryGetValue(key, out var result))
            nestedHierarchyCollections.Add(key, result = new(item.Field, node, parent, StructureVersion));
        return result.ElementType != null ? result : null;
    }
    private static bool HasHierarchyCollectionIndex(IAssetObjectNode collection, NodeIndex index)
        => (collection.Indices ?? []).Contains(index);
    private bool IsKnownHierarchyCollection(HierarchyCollectionContext context)
    {
        if (context.StructureVersion != StructureVersion || !IsKnownComponentField(context.Origin)) return false;
        var depth = 0;
        for (var current = context; ; current = current.Parent.Context)
        {
            if (++depth > 16) return false;
            if (current.Parent is not { } parent) return ReferenceEquals(current.Origin.Node.Target, current.Node);
            if (parent.Id is { } id && !parent.Collection.HasId(id) || !HasHierarchyCollectionIndex(parent.Collection, parent.Index)
                || parent.Value == null || parent.Collection.ItemReferences == null || !ReferenceEquals(parent.Collection.IndexedTarget(parent.Index), current.Node)
                || HierarchyGraph.Definition.IsTargetItemObjectReference(parent.Collection, parent.Index, parent.Value)) return false;
        }
    }
    internal bool CanReadNestedHierarchyCollection(HierarchyCollectionContext context) => IsKnownHierarchyCollection(context);
    internal bool CanEditNestedHierarchyCollection(HierarchyCollectionContext context)
        => IsKnownHierarchyCollection(context) && CanEditHierarchyField(context.Origin)
        && context.Origin.Component.Entity is { } entity && IsSelected(entity.Id)
        && context.Origin.Presentation?.IsReadOnly(context.Origin) != true && CanEditHierarchyNode(context.Node)
        && (context.Node.BaseNode == null || CollectionItemIdHelper.HasCollectionItemIds(context.Node.Retrieve()))
        && (context.Descriptor is not DictionaryDescriptor dictionary || !dictionary.IsReadOnly(context.Node.Retrieve()!));
    internal bool CanAddNestedHierarchyCollection(HierarchyCollectionContext context)
        => CanEditNestedHierarchyCollection(context) && context.Rules?.ReadOnly != true && NativePropertyClipboard.HasAdd(context.Node)
        && (context.Descriptor is DictionaryDescriptor || Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(context.ElementType!));
    internal bool CanRemoveNestedHierarchyCollection(HierarchyCollectionContext context)
        => CanEditNestedHierarchyCollection(context) && context.Rules?.ReadOnly != true && NativePropertyClipboard.HasRemove(context.Node)
        && (context.Descriptor is DictionaryDescriptor || Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(context.ElementType!));
    internal bool CanResizeNestedHierarchyCollection(HierarchyCollectionContext context)
        => CanAddNestedHierarchyCollection(context) && CanRemoveNestedHierarchyCollection(context);
    internal bool CanReorderNestedHierarchyCollection(HierarchyCollectionContext context)
        => CanResizeNestedHierarchyCollection(context) && context.Rules?.CanReorderItems == true
        && context.Descriptor is CollectionDescriptor { HasInsert: true };
    internal IReadOnlyList<HierarchyCollectionItem> GetNestedHierarchyItems(HierarchyCollectionContext context)
        => !IsKnownHierarchyCollection(context) ? [] : (context.Node.Indices ?? []).Select(index => new HierarchyCollectionItem(context, index,
            context.Node.TryIndexToId(index, out var id) && id != ItemId.Empty ? id : null)).ToArray();
    internal IReadOnlyList<ComponentField> GetNestedHierarchyItemFields(HierarchyCollectionItem item)
    {
        RefreshNestedHierarchyCollectionCache();
        if (!IsKnownHierarchyCollection(item.Context) || item.Id is { } id && !item.Collection.HasId(id)
            || !HasHierarchyCollectionIndex(item.Collection, item.Index) || item.Value == null || item.SupportsSimpleEditing
            || HierarchyGraph.Definition.IsTargetItemObjectReference(item.Collection, item.Index, item.Value)
            || item.Collection.ItemReferences == null || item.Collection.IndexedTarget(item.Index) is not { } target || target.IsEnumerable) return [];
        var key = (item.Collection, item.Index, item.Field);
        if (nestedHierarchyItemFields.TryGetValue(key, out var cached)) return cached;
        ComponentField Map(ComponentField field)
        {
            var path = item.RuntimePath(); foreach (var member in field.Path) path.Push(member);
            return field with { RuntimePath = path, Name = item.IdentityKey + "/" + field.Name, Children = field.Children.Select(Map).ToArray() };
        }
        var fields = AttachPropertyPresentation(target, BuildFields(item.Field.Component, target, [], new HashSet<IObjectNode>(), 0), item.DeclaredType).Select(Map).ToArray();
        nestedHierarchyItemFields.Add(key, fields); AddSynchronizationFields(fields); return fields;
    }
    private void ValidateNestedHierarchyItem(HierarchyCollectionItem item)
    {
        if (!CanEditNestedHierarchyCollection(item.Context) || item.Id is { } id && !item.Collection.HasId(id)
            || !HasHierarchyCollectionIndex(item.Collection, item.Index) || !CanEditHierarchyItem(item.Collection, item.Index))
            throw new InvalidOperationException("The nested collection item changed.");
    }
    internal IReadOnlyList<Type> NestedHierarchyItemTypes(HierarchyCollectionContext context)
        => context.ElementType is not { } declared || SceneObjectReference.Supports(declared) ? []
            : (Nullable.GetUnderlyingType(declared) ?? declared).GetInheritedInstantiableTypes().Append(Nullable.GetUnderlyingType(declared) ?? declared).Distinct()
                .Where(type => !type.IsAbstract && !type.IsInterface && Attribute.GetCustomAttribute(type, typeof(NonInstantiableAttribute)) == null
                    && !typeof(Stride.Engine.Entity).IsAssignableFrom(type) && !typeof(Stride.Engine.EntityComponent).IsAssignableFrom(type)
                    && !ContentAssetReference.Supports(type) && (type.IsValueType || ObjectFactoryRegistry.CanCreateInstance(type)))
                .OrderBy(type => DisplayAttribute.GetDisplayName(type) ?? type.Name).ToArray();
    private object? CreateNestedHierarchyValue(HierarchyCollectionContext context, Type? type)
    {
        object? value = null;
        if (type != null)
        {
            if (!NestedHierarchyItemTypes(context).Contains(type)) throw new ArgumentException("The collection item type is incompatible.");
            value = type.IsValueType ? Activator.CreateInstance(type) : ObjectFactoryRegistry.NewInstance(type);
            if (value != null && !type.IsValueType && value is not string)
            {
                var node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(value);
                value = AssetCloner.Clone(value, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects,
                    ExternalReferenceCollector.GetExternalReferences(HierarchyGraph.Definition, node), out _);
                AssetCollectionItemIdHelper.GenerateMissingItemIds(value!);
            }
        }
        if (value == null && (context.Rules?.NotNullItems == true || context.ElementType!.IsValueType && Nullable.GetUnderlyingType(context.ElementType!) == null))
            throw new ArgumentException("The collection does not allow null items.");
        return value;
    }
}
