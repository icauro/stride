using System.ComponentModel;
using System.Globalization;
using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal HierarchyCollectionContext? GetRootHierarchyCollection(ComponentField field)
    {
        RefreshNestedHierarchyCollectionCache();
        if (!IsKnownComponentField(field) || field.Node.Target is not IAssetObjectNode { IsEnumerable: true } node) return null;
        var key = (node, field.Component.Id + "/" + field.Name);
        if (!nestedHierarchyCollections.TryGetValue(key, out var context)) nestedHierarchyCollections.Add(key, context = new(field, node, null, StructureVersion));
        return context.ElementType != null ? context : null;
    }
    internal bool CanEditNestedHierarchyKeys(HierarchyCollectionContext context)
        => CanResizeNestedHierarchyCollection(context) && context.Descriptor is DictionaryDescriptor dictionary
        && (dictionary.KeyType == typeof(string) || TypeDescriptor.GetConverter(dictionary.KeyType).CanConvertFrom(typeof(string)));
    internal bool CanAddNestedHierarchyKeys(HierarchyCollectionContext context)
        => CanAddNestedHierarchyCollection(context) && context.Descriptor is DictionaryDescriptor dictionary && NativeDictionaryKey.CanAdd(dictionary.KeyType);
    internal void ValidateNestedHierarchyAddKey(HierarchyCollectionContext context, string text)
    {
        if (!CanAddNestedHierarchyKeys(context)) throw new InvalidOperationException("The nested dictionary key cannot be added.");
        var key = NativeDictionaryKey.CreateForAdd(((DictionaryDescriptor)context.Descriptor).KeyType, text);
        EnsureUniqueNestedHierarchyKey(context, key);
    }
    private object ParseNestedHierarchyKey(HierarchyCollectionContext context, string text)
    {
        var dictionary = context.Descriptor as DictionaryDescriptor ?? throw new ArgumentException("This collection is not a dictionary.");
        return NativeDictionaryKey.Parse(dictionary.KeyType, text);
    }
    private static void EnsureUniqueNestedHierarchyKey(HierarchyCollectionContext context, object key)
    {
        if (((DictionaryDescriptor)context.Descriptor).ContainsKey(context.Node.Retrieve()!, key)) throw new ArgumentException("This dictionary already contains that key.");
    }
    private void ValidateNestedHierarchyValue(HierarchyCollectionContext context, object? value)
    {
        var declared = context.ElementType!;
        if (value == null ? context.Rules?.NotNullItems == true || declared.IsValueType && Nullable.GetUnderlyingType(declared) == null : !declared.IsInstanceOfType(value))
            throw new ArgumentException("The collection item value is incompatible.");
        if (ContentAssetReference.Supports(Nullable.GetUnderlyingType(declared) ?? declared))
            ContentAssetReference.Validate(Nullable.GetUnderlyingType(declared) ?? declared, value, context.Rules?.NotNullItems != true);
        if (SceneObjectReference.Supports(declared) && value != null)
        {
            var entity = value is Stride.Engine.Entity target ? target : (value as Stride.Engine.EntityComponent)?.Entity;
            if (!SceneObjectReference.Accepts(declared, value) || entity == null || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part)
                || !ReferenceEquals(part.Entity, entity) || value is Stride.Engine.EntityComponent component && !entity.Components.Contains(component))
                throw new ArgumentException("The reference must belong to every selected property's document.");
        }
    }
    private void ClearNestedHierarchyOwnedReferences(HierarchyCollectionItem item, object? before)
    {
        if (before == null || HierarchyGraph.Definition.IsTargetItemObjectReference(item.Collection, item.Index, before)) return;
        var removed = item.Collection.ItemReferences != null && item.Collection.IndexedTarget(item.Index) is { } target
            ? IdentifiableObjectCollector.Collect(HierarchyGraph.Definition, target).Keys.ToHashSet() : new HashSet<Guid>();
        if (before is IIdentifiable identifiable) removed.Add(identifiable.Id);
        if (removed.Count != 0) HierarchyGraph.ClearReferencesToObjects(removed);
    }
    internal Action PrepareNestedHierarchyAdd(HierarchyCollectionContext context, Type? type, string? keyText, object? reference = null)
    {
        if (!CanAddNestedHierarchyCollection(context) || context.Descriptor is DictionaryDescriptor && !CanAddNestedHierarchyKeys(context))
            throw new InvalidOperationException("The nested collection cannot be resized.");
        var value = reference ?? CreateNestedHierarchyValue(context, type); ValidateNestedHierarchyValue(context, value);
        var key = context.Descriptor is DictionaryDescriptor ? NativeDictionaryKey.CreateForAdd(((DictionaryDescriptor)context.Descriptor).KeyType, keyText) : null;
        if (key != null) EnsureUniqueNestedHierarchyKey(context, key);
        return () =>
        {
            if (!CanAddNestedHierarchyCollection(context)) throw new InvalidOperationException("The nested collection changed.");
            if (key != null) EnsureUniqueNestedHierarchyKey(context, key);
            context.Node.Add(value!, key == null ? context.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((context.Node.Indices ?? []).Count()) : new NodeIndex(key));
        };
    }
    internal Action PrepareNestedHierarchyUpdate(HierarchyCollectionItem item, object? value)
    {
        ValidateNestedHierarchyItem(item); ValidateNestedHierarchyValue(item.Context, value);
        AssertHierarchyItemAxesEditable(item, value);
        var normalized = MatrixPropertyValue.Normalize(PropertyValueValidation.Normalize(value, HierarchyItemNumericRange(item)), item.Value);
        normalized = NativeNumericAxes.Normalize(normalized, item.Value, item.AxisNumericRanges);
        return () =>
        {
            ValidateNestedHierarchyItem(item);
            if (item.IsContentReference ? ContentAssetReference.AreEqual(item.Value, normalized) : Equals(item.Value, normalized)) return;
            item.Collection.Update(normalized, item.Index);
        };
    }
    internal bool CanChangeNestedHierarchyObject(HierarchyCollectionItem item)
        => CanEditNestedHierarchyCollection(item.Context) && CanEditHierarchyItem(item.Collection, item.Index) && !item.SupportsSimpleEditing
        && !HierarchyGraph.Definition.IsTargetItemObjectReference(item.Collection, item.Index, item.Value);
    internal Action PrepareNestedHierarchyObject(HierarchyCollectionItem item, Type? type)
    {
        ValidateNestedHierarchyItem(item);
        if (!CanChangeNestedHierarchyObject(item)) throw new InvalidOperationException("The nested collection object cannot be changed.");
        var before = item.Value;
        if (before?.GetType() == type || before == null && type == null) return () => { };
        var after = CreateNestedHierarchyValue(item.Context, type); ValidateNestedHierarchyValue(item.Context, after);
        return () =>
        {
            ValidateNestedHierarchyItem(item);
            if (!SameAggregateOwnedValue(item.Value, before)) throw new InvalidOperationException("The nested collection object changed.");
            ClearNestedHierarchyOwnedReferences(item, before); item.Collection.Update(after, item.Index);
        };
    }
    internal Action PrepareNestedHierarchyRemove(HierarchyCollectionItem item)
    {
        ValidateNestedHierarchyItem(item);
        if (!CanRemoveNestedHierarchyCollection(item.Context)) throw new InvalidOperationException("The nested collection item cannot be removed.");
        return () =>
        {
            ValidateNestedHierarchyItem(item);
            if (!CanRemoveNestedHierarchyCollection(item.Context)) throw new InvalidOperationException("The nested collection changed.");
            var value = item.Value; ClearNestedHierarchyOwnedReferences(item, value); item.Collection.Remove(value!, item.Index);
        };
    }
    internal Action PrepareNestedHierarchyMove(HierarchyCollectionItem item, int target)
    {
        ValidateNestedHierarchyItem(item);
        if (!CanReorderNestedHierarchyCollection(item.Context) || target < 0 || target >= (item.Collection.Indices ?? []).Count())
            throw new InvalidOperationException("The nested collection item cannot be reordered.");
        return () =>
        {
            ValidateNestedHierarchyItem(item);
            if (!CanReorderNestedHierarchyCollection(item.Context) || target >= (item.Collection.Indices ?? []).Count()) throw new InvalidOperationException("The nested collection changed.");
            if (item.Index.Int == target) return;
            var value = item.Value; var destination = new NodeIndex(target);
            if (item.Id is not { } id || item.Collection.BaseNode != null) { item.Collection.Remove(value!, item.Index); item.Collection.Add(value!, destination); return; }
            var itemOverride = item.Collection.GetItemOverride(item.Index); var keyOverride = item.Collection.GetKeyOverride(item.Index);
            item.Collection.RemoveAndDiscard(value!, item.Index, id); item.Collection.Restore(value!, destination, id);
            item.Collection.RestoreItemOverrides(destination, itemOverride, keyOverride);
        };
    }
    internal Action PrepareNestedHierarchyRename(HierarchyCollectionItem item, string text)
    {
        ValidateNestedHierarchyItem(item);
        if (!CanEditNestedHierarchyKeys(item.Context)) throw new InvalidOperationException("The nested dictionary key cannot be changed.");
        var key = ParseNestedHierarchyKey(item.Context, text);
        if (((DictionaryDescriptor)item.Context.Descriptor).ContainsKey(item.Collection.Retrieve()!, key)) return () => { };
        return () =>
        {
            ValidateNestedHierarchyItem(item);
            if (!CanEditNestedHierarchyKeys(item.Context)) throw new InvalidOperationException("The nested dictionary changed.");
            if (((DictionaryDescriptor)item.Context.Descriptor).ContainsKey(item.Collection.Retrieve()!, key)) return;
            var value = item.Value; var destination = new NodeIndex(key);
            if (item.Id is not { } id || item.Collection.BaseNode != null) { item.Collection.Remove(value!, item.Index); item.Collection.Add(value!, destination); return; }
            var itemOverride = item.Collection.GetItemOverride(item.Index); var keyOverride = item.Collection.GetKeyOverride(item.Index);
            item.Collection.RemoveAndDiscard(value!, item.Index, id); item.Collection.Restore(value!, destination, id);
            item.Collection.RestoreItemOverrides(destination, itemOverride, keyOverride | OverrideType.New);
        };
    }
}
