using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanSelectHierarchyItemEntry(IHierarchyCollectionValue item)
        => item.Field.Component.Entity is { } selected && IsSelected(selected.Id) && (item is CollectionItem legacy ? ReferenceEquals(legacy.Field.Node.Target, legacy.Collection) && legacy.Collection.HasId(legacy.Id)
            && CanEditCollection(legacy.Field) && CanEditHierarchyItem(legacy.Collection, legacy.Index)
            && !HierarchyGraph.Definition.IsTargetItemObjectReference(legacy.Collection, legacy.Index, legacy.Value)
        : item is HierarchyCollectionItem nested && CanEditNestedHierarchyCollection(nested.Context)
            && (nested.Id is not { } id || nested.Collection.HasId(id)) && CanEditHierarchyItem(nested.Collection, nested.Index)
            && !HierarchyGraph.Definition.IsTargetItemObjectReference(nested.Collection, nested.Index, nested.Value));
    private object? CreateHierarchyEntryValue(AbstractNodeEntry entry, object? current, Type declared, bool allowsNull)
    {
        var value = entry.GenerateValue(current!);
        if (value == null ? !allowsNull : !declared.IsInstanceOfType(value))
            throw new ArgumentException("The native collection entry returned an incompatible value.");
        if (value == null || value is string || value.GetType().IsValueType) return value;
        var node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(value);
        var result = AssetCloner.Clone(value, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects,
            ExternalReferenceCollector.GetExternalReferences(HierarchyGraph.Definition, node), out _);
        AssetCollectionItemIdHelper.GenerateMissingItemIds(result!);
        return result;
    }
    internal Action PrepareHierarchyItemEntry(IHierarchyCollectionValue item, AbstractNodeEntry entry)
    {
        if (!CanSelectHierarchyItemEntry(item) || !NativeAbstractEntries.Contains(HierarchyItemEntries(item), entry))
            throw new InvalidOperationException("The native item entry is no longer available.");
        var before = item.Value;
        if (entry.IsMatchingValue(before!)) return () => { };
        var after = CreateHierarchyEntryValue(entry, before, item.DeclaredType, item.AllowsNull);
        after = MatrixPropertyValue.Normalize(PropertyValueValidation.Normalize(after, HierarchyItemNumericRange(item)), before);
        after = NativeNumericAxes.Normalize(after, before, item.AxisNumericRanges);
        return () =>
        {
            if (!CanSelectHierarchyItemEntry(item) || !NativeAbstractEntries.Contains(HierarchyItemEntries(item), entry)
                || !SameAggregateOwnedValue(item.Value, before)) throw new InvalidOperationException("The native item or entry changed.");
            var removed = before == null || item.Collection.ItemReferences == null || item.Collection.IndexedTarget(item.Index) is not { } target ? new HashSet<Guid>()
                : IdentifiableObjectCollector.Collect(HierarchyGraph.Definition, target).Keys.ToHashSet();
            if (before is IIdentifiable identifiable) removed.Add(identifiable.Id);
            item.Collection.Update(after, item.Index);
            if (removed.Count != 0) HierarchyGraph.ClearReferencesToObjects(removed);
        };
    }
    internal Action PrepareHierarchyCollectionEntry(HierarchyCollectionContext context, AbstractNodeEntry entry, string? keyText = null)
    {
        if (!CanAddNestedHierarchyCollection(context) || !NativeAbstractEntries.Contains(HierarchyCollectionEntries(context), entry)
            || context.Descriptor is DictionaryDescriptor && !CanAddNestedHierarchyKeys(context))
            throw new InvalidOperationException("The native collection entry is no longer available.");
        var allowsNull = context.Rules?.NotNullItems != true && (!context.ElementType!.IsValueType || Nullable.GetUnderlyingType(context.ElementType) != null);
        var value = CreateHierarchyEntryValue(entry, null, context.ElementType!, allowsNull);
        ValidateNestedHierarchyValue(context, value);
        var key = context.Descriptor is DictionaryDescriptor dictionary ? NativeDictionaryKey.CreateForAdd(dictionary.KeyType, keyText) : null;
        if (key != null) EnsureUniqueNestedHierarchyKey(context, key);
        return () =>
        {
            if (!CanAddNestedHierarchyCollection(context) || !NativeAbstractEntries.Contains(HierarchyCollectionEntries(context), entry))
                throw new InvalidOperationException("The native collection or entry changed.");
            if (key != null) EnsureUniqueNestedHierarchyKey(context, key);
            var index = key != null ? new NodeIndex(key) : context.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((context.Node.Indices ?? []).Count());
            context.Node.Add(value!, index);
        };
    }
    internal void ChangeHierarchyItemEntry(IHierarchyCollectionValue item, AbstractNodeEntry entry)
    { EndEdits(); var change = PrepareHierarchyItemEntry(item, entry); ApplyInheritedGraphMutation(change, "Edit item: " + item.Field.Label); }
    internal void AddHierarchyCollectionEntry(HierarchyCollectionContext context, AbstractNodeEntry entry, string? key = null)
    { EndEdits(); var change = PrepareHierarchyCollectionEntry(context, entry, key); ApplyInheritedGraphMutation(change, "Add item: " + context.Origin.Label); }
    internal Action PrepareHierarchyDefaultCollectionAdd(HierarchyCollectionContext context)
    {
        if (!CanAddNestedHierarchyCollection(context) || context.Descriptor is not CollectionDescriptor
            || HierarchyCollectionNativeEntries(context) != null)
            throw new InvalidOperationException("The default collection command changed.");
        var instance = ObjectFactoryRegistry.NewInstance(context.ElementType!);
        var accessor = context.Parent is { } parent ? new NodeAccessor(parent.Collection, parent.Index) : new NodeAccessor(context.Origin.Node, NodeIndex.Empty);
        var value = AssetCollectionCommandRules.IsReferenceType(context.ElementType!) || HierarchyGraph.Definition.IsObjectReference(accessor, instance)
            ? null : instance;
        ValidateNestedHierarchyValue(context, value);
        return () =>
        {
            if (!CanAddNestedHierarchyCollection(context) || HierarchyCollectionNativeEntries(context) != null)
                throw new InvalidOperationException("The default collection command changed before insertion.");
            var index = context.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((context.Node.Indices ?? []).Count());
            context.Node.Add(value!, index);
        };
    }
    internal void AddHierarchyDefaultCollectionItem(HierarchyCollectionContext context)
    { EndEdits(); var change = PrepareHierarchyDefaultCollectionAdd(context); ApplyInheritedGraphMutation(change, "Add item: " + context.Origin.Label); }
}
