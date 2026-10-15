using System.ComponentModel;
using Stride.Core;
using Stride.Core.Reflection;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanEditAggregateCollection(ComponentField field)
        => SessionHistory != null && ReferenceEquals(SessionHistory.Registry.Find(Asset.Id)?.Document, this)
        && field.Component.Entity is { } entity && IsSelected(entity.Id) && CanEditHierarchyField(field)
        && (field.IsIndexedCollection || field.IsDictionary) && field.Node.Target is IAssetObjectNode collection
        && CanEditHierarchyNode(collection) && field.Presentation?.IsReadOnly(field) != true
        && (field.CollectionDescriptor is not DictionaryDescriptor dictionary || field.Node.Retrieve() is { } value && !dictionary.IsReadOnly(value));
    internal bool CanResizeAggregateCollection(ComponentField field)
        => CanEditAggregateCollection(field) && field.CollectionRules?.ReadOnly != true && field.Node.Retrieve() is { } value
        && (field.CollectionDescriptor is CollectionDescriptor { HasAdd: true } descriptor && (descriptor.HasRemoveAt || descriptor.HasRemove) && !descriptor.IsReadOnly(value)
            || field.CollectionDescriptor is DictionaryDescriptor dictionary && !dictionary.IsReadOnly(value));
    internal bool CanReorderAggregateCollection(ComponentField field)
        => CanResizeAggregateCollection(field) && field.CollectionRules?.CanReorderItems == true
        && field.CollectionDescriptor is CollectionDescriptor { HasInsert: true };
    internal bool CanEditAggregateDictionaryKeys(ComponentField field)
        => CanResizeAggregateCollection(field) && field.CollectionDescriptor is DictionaryDescriptor descriptor
        && (descriptor.KeyType == typeof(string) || TypeDescriptor.GetConverter(descriptor.KeyType).CanConvertFrom(typeof(string)));

    internal bool CanAddAggregateDictionaryKeys(ComponentField field)
        => CanResizeAggregateCollection(field) && field.CollectionDescriptor is DictionaryDescriptor descriptor && NativeDictionaryKey.CanAdd(descriptor.KeyType);
    internal void ApplyAggregateCollectionChanges(IReadOnlyList<Action> changes, string title)
        => ApplyInheritedGraphMutation(() => { foreach (var change in changes) change(); }, title);

    private void ValidateAggregateCollectionItem(CollectionItem item)
    {
        if (!CanEditAggregateCollection(item.Field) || !ReferenceEquals(item.Field.Node.Target, item.Collection)
            || !item.Collection.HasId(item.Id) || !CanEditHierarchyItem(item.Collection, item.Index))
            throw new InvalidOperationException("The selected collection item changed.");
    }
    internal Action PrepareAggregateCollectionAdd(ComponentField field, Type? type, string? dictionaryKey = null)
    {
        if (!CanEditAggregateCollection(field) || !CanResizeAggregateCollection(field)
            || field.IsDictionary && !CanAddAggregateDictionaryKeys(field))
            throw new InvalidOperationException("The selected collection cannot be resized.");
        var value = CreateCollectionValue(field, type);
        var key = field.IsDictionary ? CreateDictionaryAddKey(field, dictionaryKey) : null;
        if (key != null) EnsureUniqueDictionaryKey(field, key);
        return () =>
        {
            if (!CanEditAggregateCollection(field) || !CanResizeAggregateCollection(field))
                throw new InvalidOperationException("The selected collection changed.");
            if (key != null) EnsureUniqueDictionaryKey(field, key);
            var collection = (IAssetObjectNode)field.Node.Target!;
            collection.Add(value!, key == null ? collection.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((collection.Indices ?? []).Count()) : new NodeIndex(key));
        };
    }
    internal Action PrepareAggregateCollectionRemove(CollectionItem item)
    {
        ValidateAggregateCollectionItem(item);
        if (!CanResizeAggregateCollection(item.Field)) throw new InvalidOperationException("The selected item cannot be removed.");
        return () =>
        {
            ValidateAggregateCollectionItem(item);
            if (!CanResizeAggregateCollection(item.Field)) throw new InvalidOperationException("The selected collection changed.");
            var value = item.Value; var index = item.Index; var removed = new HashSet<Guid>();
            if (value != null && !HierarchyGraph.Definition.IsTargetItemObjectReference(item.Collection, index, value))
            {
                if (item.Collection.ItemReferences != null && item.Collection.IndexedTarget(index) is { } target)
                    removed.UnionWith(IdentifiableObjectCollector.Collect(HierarchyGraph.Definition, target).Keys);
                if (value is IIdentifiable identifiable) removed.Add(identifiable.Id);
            }
            if (removed.Count != 0) HierarchyGraph.ClearReferencesToObjects(removed);
            item.Collection.Remove(value!, item.Collection.IdToIndex(item.Id));
        };
    }
    internal Action PrepareAggregateCollectionMove(CollectionItem item, int target)
    {
        ValidateAggregateCollectionItem(item);
        if (!CanReorderAggregateCollection(item.Field) || target < 0 || target >= (item.Collection.Indices ?? []).Count())
            throw new InvalidOperationException("The selected item cannot be reordered.");
        return () =>
        {
            ValidateAggregateCollectionItem(item);
            if (!CanReorderAggregateCollection(item.Field) || target >= (item.Collection.Indices ?? []).Count())
                throw new InvalidOperationException("The selected collection changed.");
            if (item.Index.Int == target) return;
            var value = item.Value; var itemOverride = item.Collection.GetItemOverride(item.Index); var keyOverride = item.Collection.GetKeyOverride(item.Index);
            if (item.Collection.BaseNode != null)
            {
                item.Collection.Remove(value!, item.Index);
                item.Collection.Add(value!, new NodeIndex(target));
                return;
            }
            item.Collection.RemoveAndDiscard(value!, item.Index, item.Id);
            var destination = new NodeIndex(target);
            item.Collection.Restore(value!, destination, item.Id);
            item.Collection.RestoreItemOverrides(destination, itemOverride, keyOverride);
        };
    }
    internal Action PrepareAggregateDictionaryRename(CollectionItem item, string text)
    {
        ValidateAggregateCollectionItem(item);
        if (!CanEditAggregateDictionaryKeys(item.Field)) throw new InvalidOperationException("The selected key cannot be changed.");
        var key = ParseDictionaryKey(item.Field, text);
        if (((DictionaryDescriptor)item.Field.CollectionDescriptor).ContainsKey(item.Collection.Retrieve()!, key)) return () => { };
        return () =>
        {
            ValidateAggregateCollectionItem(item);
            if (!CanEditAggregateDictionaryKeys(item.Field)) throw new InvalidOperationException("The selected dictionary changed.");
            if (((DictionaryDescriptor)item.Field.CollectionDescriptor).ContainsKey(item.Collection.Retrieve()!, key)) return;
            var value = item.Value; var itemOverride = item.Collection.GetItemOverride(item.Index); var keyOverride = item.Collection.GetKeyOverride(item.Index);
            if (item.Collection.BaseNode != null)
            {
                item.Collection.Remove(value!, item.Index);
                item.Collection.Add(value!, new NodeIndex(key));
                return;
            }
            item.Collection.RemoveAndDiscard(value!, item.Index, item.Id);
            var destination = new NodeIndex(key);
            item.Collection.Restore(value!, destination, item.Id);
            item.Collection.RestoreItemOverrides(destination, itemOverride, keyOverride | OverrideType.New);
        };
    }
}
