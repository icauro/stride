using System.Globalization;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal IReadOnlyList<UIPropertyCollection> PropertyCollections(Guid id)
    {
        var result = new List<UIPropertyCollection>();
        var visited = new HashSet<IObjectNode>();
        Visit(ObjectNode(Require(id)), "", "", 0);
        var dependencyMember = ObjectNode(Require(id))[nameof(UIElement.DependencyProperties)];
        var dependencies = DependencyPropertyNode(id);
        foreach (var property in AttachedProperties(id))
        {
            var index = new NodeIndex(property.Key);
            if (!dependencies.Indices.Contains(index) || dependencies.IndexedTarget(index) is not IAssetObjectNode target
                || AuthoringGraph.Definition.IsTargetItemObjectReference(dependencies, index, dependencies.Retrieve(index))) continue;
            var path = "@" + property.Key.OwnerType.FullName + "/" + property.Key.Name;
            if (target.IsEnumerable && PropertyClipboardRules.ElementType(target) is { } type && !typeof(UIElement).IsAssignableFrom(type))
            {
                VisitCollection(target, dependencyMember, path, property.Label, 0, property.Key, true);
            }
            else if (!target.IsEnumerable) Visit(target, path, property.Label, 1, property.Key);
        }
        return result;
        void Visit(IObjectNode owner, string path, string label, int depth, PropertyKey? attachedKey = null)
        {
            if (depth > 8 || !visited.Add(owner) || UnknownAssetData.IsProxyNode(owner)) return;
            foreach (var member in owner.Members)
            {
                if (member.Name is nameof(UIElement.Id) or nameof(Panel.Children) or nameof(ContentControl.Content) or nameof(UIElement.DependencyProperties)) continue;
                var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(member.MemberDescriptor.MemberInfo);
                if (display?.Browsable == false || member.Target is not IAssetObjectNode target
                    || AuthoringGraph.Definition.IsMemberTargetObjectReference(member, member.Retrieve())) continue;
                var leaf = display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(member.Name);
                var itemPath = path.Length == 0 ? member.Name : path + "/" + member.Name;
                var itemLabel = label.Length == 0 ? leaf : label + " / " + leaf;
                if (target.IsEnumerable && PropertyClipboardRules.ElementType(target) is { } type && !typeof(UIElement).IsAssignableFrom(type))
                {
                    VisitCollection(target, member, itemPath, itemLabel, depth, attachedKey, false);
                }
                else if (!target.IsEnumerable) Visit(target, itemPath, itemLabel, depth + 1, attachedKey);
            }
        }
        void VisitCollection(IAssetObjectNode target, IMemberNode member, string path, string label, int depth, PropertyKey? key, bool attachedRoot)
        {
            if (depth > 8 || !visited.Add(target) || UnknownAssetData.IsProxyNode(target)) return;
            if (PropertyClipboardRules.ElementType(target) is not { } elementType || typeof(UIElement).IsAssignableFrom(elementType)) return;
            result.Add(new(id, member, target, path, label, key, attachedRoot));
            foreach (var index in target.Indices ?? [])
            {
                if (target.ItemReferences == null || AuthoringGraph.Definition.IsTargetItemObjectReference(target, index, target.Retrieve(index)) || target.IndexedTarget(index) is not IAssetObjectNode child) continue;
                // Matching selected property shapes follows the native index; mutation targets retain actual nodes/ItemIds.
                var childPath = path + "[" + index + "]";
                var childLabel = label + " [" + index + "]";
                if (child.IsEnumerable) VisitCollection(child, member, childPath, childLabel, depth + 1, key, false);
                else Visit(child, childPath, childLabel, depth + 1, key);
            }
        }
    }
    internal bool OwnsCollection(UIPropertyCollection collection)
        => Find(collection.ElementId) != null && PropertyCollections(collection.ElementId)
            .Any(current => ReferenceEquals(current.Member, collection.Member) && ReferenceEquals(current.Collection, collection.Collection) && ReferenceEquals(current.AttachedKey, collection.AttachedKey));
    internal bool CanEditCollection(UIPropertyCollection collection) => OwnsCollection(collection) && collection.Rules?.ReadOnly != true
        && CanEditAssetCollection(collection.Collection)
        && (collection.AttachedKey == null || CanChangeDependencyProperty(collection.ElementId, collection.AttachedKey));
    internal bool CanEditCollectionItem(UIPropertyCollectionItem item)
        => CanEditCollection(item.Owner) && (item.Id is { } id ? item.Owner.Collection.HasId(id) : (item.Owner.Collection.Indices ?? []).Contains(item.OriginalIndex))
            && CanEditAssetSettingItem(item.Owner.Collection, item.Index);
    internal bool CanResizeCollection(UIPropertyCollection collection) => CanEditCollection(collection)
        && NativePropertyClipboard.HasAdd(collection.Collection) && NativePropertyClipboard.HasRemove(collection.Collection);
    internal bool CanAddCollectionItem(UIPropertyCollection collection) => CanEditCollection(collection)
        && NativePropertyClipboard.HasAdd(collection.Collection) && AssetCollectionCommandRules.CanAdd(collection.ElementType);
    internal bool CanRemoveCollectionItem(UIPropertyCollectionItem item) => CanEditCollectionItem(item)
        && NativePropertyClipboard.HasRemove(item.Owner.Collection)
        && (item.Owner.Collection.Descriptor is DictionaryDescriptor || AssetCollectionCommandRules.CanAdd(item.Owner.ElementType));
    internal bool CanMoveCollectionItem(UIPropertyCollectionItem item) => CanEditCollectionItem(item) && CanResizeCollection(item.Owner)
        && item.Owner.Rules?.CanReorderItems == true && item.Owner.Collection.Descriptor is CollectionDescriptor { HasInsert: true };
    internal void TransformCollectionItems(IReadOnlyList<UIPropertyCollectionItem> items, Func<object?, object?> transform)
    {
        EndEdit();
        if (items.Count == 0 || items.Any(item => !CanEditCollectionItem(item))) throw new InvalidOperationException("The selected UI collection items cannot be edited.");
        var prepared = items.Select(item =>
        {
            var proposed = transform(item.Value);
            AssertUIItemAxesEditable(item.Owner.Collection, item.Index, item.Value, proposed);
            return (Item: item, Value: NativeNumericAxes.Normalize(
                PropertyValueValidation.Normalize(proposed, UIItemNumericRange(item.Owner.Collection, item.Index)), item.Value, UIAxisNumericRanges(item)));
        }).ToArray();
        foreach (var item in prepared)
        {
            ValidateCollectionValue(item.Item.Owner, item.Value);
            if (item.Value != null && !ReferenceEquals(item.Value, item.Item.Value)) AssetCollectionItemIdHelper.GenerateMissingItemIds(item.Value);
        }
        Mutate(() => { foreach (var item in prepared) item.Item.Owner.Collection.Update(item.Value, item.Item.Index); }, "Edit UI collection item");
    }
    internal void BatchCollectionEdits(Action edit, string title)
    {
        EndEdit();
        if (SessionHistory == null) throw new InvalidOperationException("The UI document has no canonical history.");
        using var capture = SessionHistory.CaptureOperationsAtomic(edit);
        capture.Publish(SessionHistory, title);
    }
    internal bool CanPasteCollectionItems(IReadOnlyList<UIPropertyCollectionItem> items, string text)
        => items.Count != 0 && items.All(CanEditCollectionItem)
            && CanPasteAssetAccessors(items.Select(item => new NodeAccessor(item.Owner.Collection, item.Index)).ToArray(), text);
    internal void PasteCollectionItems(IReadOnlyList<UIPropertyCollectionItem> items, string text, bool replace)
    {
        EndEdit();
        if (!CanPasteCollectionItems(items, text)) throw new InvalidOperationException("The selected UI collection items cannot be pasted.");
        PasteAssetAccessors(items.Select(item => new NodeAccessor(item.Owner.Collection, item.Index)).ToArray(), text, replace);
    }
    private NativePropertyClipboard CollectionClipboard(UIPropertyCollection collection)
        => new(AuthoringGraph, Asset,
            member => CanEditAssetSettingNode(member) && CanAssignAuthoringNode(member),
            (node, index) => ReferenceEquals(node, collection.Collection) && CanEditCollection(collection)
                && CanEditAssetSettingItem(collection.Collection, index),
            node => ReferenceEquals(node, collection.Collection) && CanResizeCollection(collection),
            node => ReferenceEquals(node, collection.Collection) && CanResizeCollection(collection),
            acceptItem: (node, index, value) => { ValidateCollectionValue(collection, value); return true; });
    internal bool CanPasteUICollection(UIPropertyCollection collection, string text)
        => CanEditCollection(collection) && CollectionClipboard(collection).CanPaste(text, collection.Collection.Type);
    internal void PasteUICollection(IReadOnlyList<UIPropertyCollection> collections, string text, bool replace)
    {
        EndEdit();
        if (collections.Count == 0 || collections.Any(collection => !CanPasteUICollection(collection, text)))
            throw new InvalidOperationException("The selected UI collections cannot be pasted.");
        Mutate(() =>
        {
            foreach (var collection in collections.DistinctBy(collection => collection.Collection))
                CollectionClipboard(collection).Paste(text, collection.Collection.Type, new(collection.Collection, NodeIndex.Empty), replace);
        }, replace ? "Replace property" : "Paste property");
    }
    private static void ValidateCollectionValue(UIPropertyCollection collection, object? value)
    {
        var type = collection.ElementType;
        if (value == null ? collection.Rules?.NotNullItems == true || type.IsValueType && Nullable.GetUnderlyingType(type) == null : !type.IsInstanceOfType(value))
            throw new ArgumentException("The collection item has an incompatible value.");
        if (ContentAssetReference.Supports(type)) ContentAssetReference.Validate(type, value, collection.Rules?.NotNullItems != true);
    }
    internal IReadOnlyList<Type> CollectionItemTypes(UIPropertyCollection collection)
    {
        var type = Nullable.GetUnderlyingType(collection.ElementType) ?? collection.ElementType;
        if (ContentAssetReference.Supports(type)) return [];
        var content = AssetRegistry.CanPropertyHandleContent(type, out var types) ? types : [];
        return type.GetInheritedInstantiableTypes().Append(type).Distinct().Where(candidate => !candidate.IsAbstract && !candidate.IsInterface
            && !typeof(UIElement).IsAssignableFrom(candidate) && !content.Contains(candidate) && !candidate.IsDefined(typeof(NonInstantiableAttribute), true)
            && (candidate.IsValueType || candidate == typeof(string) || ObjectFactoryRegistry.CanCreateInstance(candidate))).ToArray();
    }
    internal void AddCollectionItem(UIPropertyCollection collection, Type? type, string? key = null, object? reference = null)
    {
        EndEdit();
        if (!CanAddCollectionItem(collection)) throw new InvalidOperationException("The UI collection cannot be resized.");
        if (type != null && reference == null && !CollectionItemTypes(collection).Contains(type)) throw new InvalidOperationException("The collection item type is unavailable.");
        var value = reference ?? (type == null ? null : type == typeof(string) ? "" : ObjectFactoryRegistry.NewInstance(type));
        ValidateCollectionValue(collection, value);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        var index = collection.Collection.Descriptor is DictionaryDescriptor dictionary ? new NodeIndex(ParseCollectionKey(dictionary.KeyType, key ?? ""))
            : collection.Collection.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((collection.Collection.Indices ?? []).Count());
        if ((collection.Collection.Indices ?? []).Contains(index)) throw new ArgumentException("The collection already contains this key.");
        Mutate(() => collection.Collection.Add(value, index), "Add UI collection item");
    }
    private static object ParseCollectionKey(Type type, string text) => NativeDictionaryKey.CreateForAdd(type, text);
    internal bool CanRenameCollectionKey(UIPropertyCollectionItem item)
        => CanEditCollectionItem(item) && CanResizeCollection(item.Owner) && item.Id != null
            && item.Owner.Collection.Descriptor is DictionaryDescriptor dictionary
            && NativeDictionaryKey.SupportsText(dictionary.KeyType);
    internal void RenameCollectionKey(UIPropertyCollectionItem item, string text) => RenameUICollectionKeys([item], text);
    internal void RemoveCollectionItem(UIPropertyCollectionItem item)
    {
        EndEdit();
        if (!CanRemoveCollectionItem(item)) throw new InvalidOperationException("The collection item cannot be removed.");
        Mutate(() => item.Owner.Collection.Remove(item.Value, item.Index), "Remove UI collection item");
    }
    internal void MoveCollectionItem(UIPropertyCollectionItem item, int destination)
    {
        EndEdit();
        if (!CanMoveCollectionItem(item)) throw new InvalidOperationException("The collection item cannot be reordered.");
        var count = (item.Owner.Collection.Indices ?? []).Count();
        if (destination < 0 || destination >= count) throw new ArgumentOutOfRangeException(nameof(destination));
        var collection = item.Owner.Collection; var id = item.Id; var value = item.Value; var before = item.Index;
        var itemOverride = collection.GetItemOverride(before); var keyOverride = collection.GetKeyOverride(before);
        Mutate(() =>
        {
            var target = new NodeIndex(destination);
            if (id is { } retained && (collection.BaseNode is not IAssetObjectNode source || !source.HasId(retained)))
            {
                collection.RemoveAndDiscard(value, collection.IdToIndex(retained), retained); collection.Restore(value, target, retained);
                collection.RestoreItemOverrides(target, itemOverride, keyOverride);
            }
            else
            {
                collection.Remove(value, before); collection.Add(value, target);
                collection.RestoreItemOverrides(target, itemOverride | OverrideType.New, keyOverride);
            }
        }, "Move UI collection item");
    }
}
