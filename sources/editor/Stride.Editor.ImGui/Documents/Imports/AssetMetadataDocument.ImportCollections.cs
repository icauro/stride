using System.Globalization;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly HashSet<AssetImportSettingItem> importSettingsItems = new(ReferenceEqualityComparer.Instance);
    private AssetImportSettingItem? activeImportItem;
    private object? importItemStart, importItemPending;
    private bool HasImportItemChange => activeImportItem != null && !ContentAssetReference.AreEqual(importItemStart, importItemPending);
    private IReadOnlyList<AssetImportSettingItem> ReadImportCollectionItems(AssetImportSettingField field, int depth, HashSet<IObjectNode> visited)
    {
        var collection = field.Collection!;
        return (collection.Indices ?? []).Select(index =>
        {
            var value = collection.Retrieve(index);
            ItemId? id = collection.TryIndexToId(index, out var actualId) ? actualId : null;
            // Scalar collections expose indexed values without Quantum object references.
            var itemTarget = collection.ItemReferences != null ? collection.IndexedTarget(index) : null;
            var children = value != null && value is not Stride.Core.Yaml.IUnloadable && !IsImportSettingScalar(value.GetType()) && !ContentAssetReference.Supports(value.GetType())
                && !importSettingsGraph!.Definition.IsTargetItemObjectReference(collection, index, value)
                && itemTarget is { IsEnumerable: false } target
                    ? ReadImportSettingsFields(target, field.Key + "/" + (id?.ToString() ?? index.ToString()), depth + 1, visited) : [];
            AssetImportSettingField? nested = null;
            if (depth < 12 && value != null && value is not Stride.Core.Yaml.IUnloadable && !importSettingsGraph!.Definition.IsTargetItemObjectReference(collection, index, value)
                && itemTarget is IAssetObjectNode { IsEnumerable: true } nestedCollection
                && visited.Add(nestedCollection))
            {
                nested = new(field.Node, field.Key + "/" + (id?.ToString() ?? index.ToString()) + "/Items", index.ToString(), "", 0)
                    { IndexedCollection = nestedCollection };
                importSettingsMembers.Add(nested);
                nested.Items = ReadImportCollectionItems(nested, depth + 1, visited);
            }
            var item = new AssetImportSettingItem(field, collection, index, id, children) { NestedCollection = nested };
            importSettingsItems.Add(item); return item;
        }).ToArray();
    }
    public bool CanEditImportCollection(AssetImportSettingField field) => (HasImportSettings || MaterialSettings != null) && SessionHistory != null && CanEditArchetypeAsset
        && (importSettingsMembers.Contains(field) || importSettingsMembers.Any(current => ReferenceEquals(current.Node, field.Node) && current.Key == field.Key))
        && field.Collection is { } collection && CanEditAssetSettingNode(collection)
        && collection.Retrieve() != null && (collection.Descriptor is not DictionaryDescriptor dictionary || !dictionary.IsReadOnly(collection.Retrieve()));
    public bool CanAddImportCollection(AssetImportSettingField field) => CanEditImportCollection(field) && field.CollectionRules?.ReadOnly != true
        && NativePropertyClipboard.HasAdd(field.Collection!) && field.ElementType is { } type && AssetCollectionCommandRules.CanAdd(type);
    public bool CanRemoveImportCollection(AssetImportSettingField field) => CanEditImportCollection(field) && field.CollectionRules?.ReadOnly != true
        && NativePropertyClipboard.HasRemove(field.Collection!) && (field.Collection!.Descriptor is DictionaryDescriptor
            || field.ElementType is { } type && AssetCollectionCommandRules.CanAdd(type));
    public bool CanResizeImportCollection(AssetImportSettingField field) => CanAddImportCollection(field) && CanRemoveImportCollection(field);
    public bool CanReorderImportCollection(AssetImportSettingField field) => CanResizeImportCollection(field)
        && field.CollectionRules?.CanReorderItems == true && field.Collection!.Descriptor is CollectionDescriptor { HasInsert: true };
    public bool CanEditImportItem(AssetImportSettingItem item) => (importSettingsItems.Contains(item)
        || importSettingsItems.Any(current => ReferenceEquals(current.Collection, item.Collection) && current.Key == item.Key)) && CanEditImportCollection(item.Field)
        && (item.Id is not { } id || item.Collection.HasId(id)) && CanEditAssetSettingItem(item.Collection, item.Index)
        && !IsImportItemPresentationReadOnly(item);
    public IReadOnlyList<Type> ImportCollectionItemTypes(AssetImportSettingField field)
        => field.ElementType is { } declared && !ContentAssetReference.Supports(declared)
            ? (Nullable.GetUnderlyingType(declared) ?? declared).GetInheritedInstantiableTypes().Append(Nullable.GetUnderlyingType(declared) ?? declared).Distinct()
                .Where(type => !type.IsAbstract && !type.IsInterface && !type.IsDefined(typeof(NonInstantiableAttribute), true)
                    && (type.IsValueType || type == typeof(string) || ObjectFactoryRegistry.CanCreateInstance(type))).OrderBy(type => DisplayAttribute.GetDisplayName(type)).ToArray() : [];
    private object? CreateImportCollectionValue(AssetImportSettingField field, Type? type, object? reference)
    {
        if (reference != null)
        {
            if (ContentAssetReference.Supports(field.ElementType!)) ContentAssetReference.Validate(field.ElementType!, reference, false);
            else if (!field.ElementType!.IsInstanceOfType(reference)) throw new ArgumentException("The collection value is not compatible.");
            return PropertyValueValidation.Normalize(reference, null);
        }
        if (type == null)
        {
            if (field.CollectionRules?.NotNullItems == true || field.ElementType!.IsValueType && Nullable.GetUnderlyingType(field.ElementType) == null)
                throw new InvalidOperationException("This collection cannot contain null.");
            return null;
        }
        if (!ImportCollectionItemTypes(field).Contains(type)) throw new InvalidOperationException("This collection item type cannot be selected.");
        var value = type == typeof(string) ? "" : ObjectFactoryRegistry.NewInstance(type);
        AssetCollectionItemIdHelper.GenerateMissingItemIds(value); return value;
    }
    public void AddImportCollectionItem(AssetImportSettingField field, Type? type, object? reference = null, string? key = null)
    {
        if (!CanAddImportCollection(field)) throw new InvalidOperationException("This collection cannot accept items.");
        var value = CreateImportCollectionValue(field, type, reference); var collection = field.Collection!;
        var index = collection.Descriptor is DictionaryDescriptor dictionary
            ? new NodeIndex(NativeDictionaryKey.CreateForAdd(dictionary.KeyType, key)) : collection.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((collection.Indices ?? []).Count());
        if (collection.Descriptor is DictionaryDescriptor map && map.ContainsKey(collection.Retrieve(), index.Value)) throw new ArgumentException("The dictionary already contains that key.");
        ApplyInheritedAssetMutation(() => collection.Add(value!, index), "Add " + field.Label);
    }
    private static object ParseImportDictionaryKey(Type type, string key) => NativeDictionaryKey.Parse(type, key);
    public object? ReadImportItem(AssetImportSettingItem item) => ReferenceEquals(item, activeImportItem) ? importItemPending : item.Value;
    public void UpdateImportItem(AssetImportSettingItem item, object? value)
    {
        if (!CanEditImportItem(item)) throw new InvalidOperationException("This collection item cannot be edited.");
        AssertImportItemAxesEditable(item, item.Value, value);
        value = PropertyValueValidation.Normalize(value, ImportItemNumericRange(item));
        if (item.Type != typeof(Stride.Core.Mathematics.Quaternion) && item.Type != typeof(Stride.Core.Mathematics.Color)
            && item.Type != typeof(Stride.Core.Mathematics.Color3) && item.Type != typeof(Stride.Core.Mathematics.Color4))
            value = NativeNumericAxes.Normalize(value, item.Value, ImportItemAxisNumericRanges(item));
        if (value == null ? !item.AllowsNull : !item.Type.IsInstanceOfType(value)) throw new ArgumentException("The collection item value is not compatible.");
        if (ContentAssetReference.Supports(item.Type)) ContentAssetReference.Validate(item.Type, value, item.AllowsNull);
        if (!ReferenceEquals(activeImportItem, item)) { CommitEdits(); activeImportItem = item; importItemStart = item.Value; }
        importItemPending = value;
    }
    private void EndImportSettingItemEdit()
    {
        if (activeImportItem == null) return;
        var item = activeImportItem; var before = importItemStart; var after = importItemPending;
        activeImportItem = null; importItemStart = importItemPending = null;
        if (ContentAssetReference.AreEqual(before, after)) return;
        if (!CanEditImportItem(item)) throw new InvalidOperationException("This collection item cannot be edited.");
        ApplyInheritedAssetMutation(() => item.Collection.Update(after, item.Index), "Edit " + item.Field.Label);
    }
    public void ChangeImportCollectionItemType(AssetImportSettingItem item, Type? type)
    {
        if (!CanEditImportItem(item)) throw new InvalidOperationException("This collection item cannot be edited.");
        var value = CreateImportCollectionValue(item.Field, type, null);
        ApplyInheritedAssetMutation(() => { ClearRemovedImportItemReferences(item); item.Collection.Update(value, item.Index); }, "Edit " + item.Field.Label);
    }
    public void RemoveImportCollectionItem(AssetImportSettingItem item)
    {
        if (!CanEditImportItem(item) || !CanRemoveImportCollection(item.Field)) throw new InvalidOperationException("This collection item cannot be removed.");
        ApplyInheritedAssetMutation(() => { ClearRemovedImportItemReferences(item); item.Collection.Remove(item.Value!, item.Index); }, "Remove " + item.Field.Label);
    }
    private void ClearRemovedImportItemReferences(AssetImportSettingItem item)
    {
        if (item.Value is not { } value || importSettingsGraph!.Definition.IsTargetItemObjectReference(item.Collection, item.Index, value)) return;
        var ids = item.Collection.ItemReferences != null && item.Collection.IndexedTarget(item.Index) is { } target
            ? IdentifiableObjectCollector.Collect(importSettingsGraph.Definition, target).Keys.ToHashSet() : new HashSet<Guid>();
        if (value is IIdentifiable identifiable) ids.Add(identifiable.Id);
        if (ids.Count != 0) importSettingsGraph.ClearReferencesToObjects(ids);
    }
    public void MoveImportCollectionItem(AssetImportSettingItem item, int destination)
    {
        if (!CanEditImportItem(item) || !CanReorderImportCollection(item.Field) || !item.Index.IsInt || item.Id is not { } id)
            throw new InvalidOperationException("This collection item cannot be moved.");
        var collection = item.Collection;
        if (destination < 0 || destination >= (collection.Indices ?? []).Count()) throw new ArgumentOutOfRangeException(nameof(destination));
        ApplyInheritedAssetMutation(() =>
        {
            var index = collection.IdToIndex(id); var value = collection.Retrieve(index);
            var overridden = collection.IsItemOverridden(index); collection.RemoveAndDiscard(value!, index, id);
            collection.Restore(value!, new NodeIndex(destination), id); collection.OverrideItem(overridden, new NodeIndex(destination));
        }, "Move " + item.Field.Label);
    }
    public bool CanRenameImportCollectionKey(AssetImportSettingItem item) => CanEditImportItem(item) && item.Field.CollectionRules?.ReadOnly != true
        && NativePropertyClipboard.HasAdd(item.Collection) && NativePropertyClipboard.HasRemove(item.Collection)
        && item.Collection.Descriptor is DictionaryDescriptor dictionary && NativeDictionaryKey.SupportsText(dictionary.KeyType) && item.Id != null;
    public void RenameImportCollectionKey(AssetImportSettingItem item, string text)
    {
        if (!CanRenameImportCollectionKey(item)) throw new InvalidOperationException("This dictionary key cannot be edited.");
        var collection = item.Collection; var dictionary = (DictionaryDescriptor)collection.Descriptor;
        var key = ParseImportDictionaryKey(dictionary.KeyType, text);
        if (Equals(key, item.Index.Value)) return;
        if (dictionary.ContainsKey(collection.Retrieve(), key)) return;
        ApplyInheritedAssetMutation(() =>
        {
            var index = item.Index; var value = collection.Retrieve(index);
            var itemOverride = collection.IsItemOverridden(index); var keyOverride = collection.IsKeyOverridden(index);
            collection.RemoveAndDiscard(value!, index, item.Id!.Value);
            var next = new NodeIndex(key); collection.Restore(value!, next, item.Id.Value);
            collection.OverrideItem(itemOverride, next); collection.OverrideKey(keyOverride, next);
        }, "Rename key");
    }
}
