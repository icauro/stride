using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Assets;
using Stride.Core.Annotations;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    internal ProceduralCollectionContext? CollectionContext(AssetPropertyField field)
        => IsPropertyFieldCurrent(field) && field.Node.Target is IAssetObjectNode { IsEnumerable: true } node
            ? new(field, node) : null;
    internal ProceduralCollectionContext? CollectionContext(IAssetObjectNode node)
    {
        var visited = new HashSet<IObjectNode>();
        ProceduralCollectionContext? Find(ProceduralCollectionContext context)
        {
            if (!visited.Add(context.Node)) return null;
            if (ReferenceEquals(context.Node, node)) return context;
            foreach (var item in CollectionItems(context))
                if (context.Node.ItemReferences != null
                    && context.Node.IndexedTarget(item.Index) is IAssetObjectNode { IsEnumerable: true } nested
                    && Find(new(context.Origin, nested, item)) is { } match) return match;
            return null;
        }
        foreach (var member in proceduralMembers)
            if (CollectionContext(PropertyField(member)) is { } root && Find(root) is { } match) return match;
        return null;
    }
    internal bool IsCollectionCurrent(ProceduralCollectionContext context)
    {
        var visited = new HashSet<IObjectNode>();
        for (var current = context; ; current = current.Parent.Context)
        {
            if (!visited.Add(current.Node) || !IsPropertyFieldCurrent(current.Origin)) return false;
            if (current.Parent is not { } parent) return ReferenceEquals(current.Origin.Node.Target, current.Node);
            if (!IsCollectionItemCurrent(parent) || !ReferenceEquals(parent.Context.Node.IndexedTarget(parent.Index), current.Node)) return false;
        }
    }
    internal bool IsCollectionItemCurrent(ProceduralCollectionItem item)
        => IsCollectionCurrent(item.Context) && (item.Id is { } id ? item.Context.Node.HasId(id) : (item.Context.Node.Indices ?? []).Contains(item.OriginalIndex));
    internal IReadOnlyList<ProceduralCollectionItem> CollectionItems(ProceduralCollectionContext context)
        => !IsCollectionCurrent(context) ? [] : (context.Node.Indices ?? []).Select(index =>
            new ProceduralCollectionItem(context, index, context.Node.TryIndexToId(index, out var id) && id != ItemId.Empty ? id : null)).ToArray();
    internal INodePresenter? CollectionItemPresenter(ProceduralCollectionItem item)
        => IsCollectionItemCurrent(item) ? NumericPresentation?.ItemPresenter(item.Context.Node, item.Index) : null;
    internal bool CanEditCollection(ProceduralCollectionContext context)
        => IsCollectionCurrent(context) && CanEditNativeCollection(context.Node)
            && !PropertyClipboardRules.IsReadOnly(context.Origin.Node)
            && (context.Node.BaseNode == null || CollectionItemIdHelper.HasCollectionItemIds(context.Node.Retrieve()));
    internal bool CanEditCollectionItem(ProceduralCollectionItem item)
        => IsCollectionItemCurrent(item) && CanEditCollection(item.Context)
            && !AuthoringGraph.IsInheritedNodeSealed(item.Context.Node, item.Index)
            && (CollectionItemPresenter(item) is not { } presenter || !EditorPresenterItemMetadata.IsReadOnly(presenter));
    internal bool CanAddCollection(ProceduralCollectionContext context)
        => CanEditCollection(context) && context.Rules?.ReadOnly != true && NativePropertyClipboard.HasAdd(context.Node)
            && Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(context.ElementType)
            && (context.Node.Descriptor is not DictionaryDescriptor dictionary || NativeDictionaryKey.CanAdd(dictionary.KeyType));
    internal bool CanRemoveCollectionItem(ProceduralCollectionItem item)
        => CanEditCollectionItem(item) && item.Context.Rules?.ReadOnly != true && NativePropertyClipboard.HasRemove(item.Context.Node);
    internal IEnumerable<AbstractNodeEntry>? CollectionNativeEntries(ProceduralCollectionContext context)
    {
        var presenter = context.Parent is { } parent ? CollectionItemPresenter(parent) : NumericPresentation?.Find(context.Origin.Node);
        return presenter != null && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries : null;
    }
    internal IReadOnlyList<AbstractNodeEntry> CollectionEntries(ProceduralCollectionContext context)
        => CollectionNativeEntries(context)?.OrderBy(entry => entry.Order).ToArray() ?? [];
    internal IReadOnlyList<AbstractNodeEntry> CollectionItemEntries(ProceduralCollectionItem item)
        => CollectionItemPresenter(item) is { } presenter && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries)
            ? entries.OrderBy(entry => entry.Order).ToArray() : [];
    internal EditorPresenterTarget? AcquireCollectionTarget(ProceduralCollectionContext context)
        => IsCollectionCurrent(context) && NumericPresentation is { } presentation
            && EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, context.Node) is { } presenter
            ? presentation.AcquireTarget(presenter) : null;
    internal EditorPresenterTarget? AcquireCollectionItemTarget(ProceduralCollectionItem item)
        => CollectionItemPresenter(item) is { } presenter ? NumericPresentation?.AcquireTarget(presenter) : null;
    internal bool CanEditNativeCollection(IObjectNode node)
        => CanEditAssetSettingNode(node)
            && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(node.Type)?.ReadOnly != true
            && !proceduralMembers.Any(member => ReferenceEquals(member.Target, node) && PropertyClipboardRules.IsReadOnly(member))
            && (NumericPresentation == null
            || EditorPresenterItemMetadata.FindCollection(NumericPresentation.RootPresenter, node) is not { } presenter
            // A getter-only MemberNodePresenter is read-only for replacement, not for
            // native AddNewItem/AddPrimitiveKey/RemoveItem on its mutable target.
            || (presenter is not ItemNodePresenter || !presenter.IsReadOnly)
                && !EditorPresenterItemMetadata.HasReadOnlyItemAncestor(presenter));
    internal bool CanEditNativeCollectionItem(IObjectNode node, NodeIndex index)
        => node is IAssetObjectNode collection && CanEditNativeCollection(node) && !AuthoringGraph.IsInheritedNodeSealed(collection, index)
            && (NumericPresentation?.ItemPresenter(node, index) is not { } presenter || !EditorPresenterItemMetadata.IsReadOnly(presenter));
    internal bool CanAddNativeCollection(IObjectNode node)
        => CanEditNativeCollection(node) && NativePropertyClipboard.HasAdd(node)
            && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(node.Type)?.ReadOnly != true
            && !proceduralMembers.Where(member => ReferenceEquals(member.Target, node)).Any(member => PropertyClipboardRules.IsReadOnly(member));
    internal bool CanRemoveNativeCollection(IObjectNode node)
        => CanEditNativeCollection(node) && NativePropertyClipboard.HasRemove(node)
            && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(node.Type)?.ReadOnly != true
            && !proceduralMembers.Where(member => ReferenceEquals(member.Target, node)).Any(member => PropertyClipboardRules.IsReadOnly(member));
    private bool NativeCollectionNotNullItems(IObjectNode node)
        => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(node.Type)?.NotNullItems == true
            || proceduralMembers.Any(member => ReferenceEquals(member.Target, node) && PropertyClipboardRules.NotNullItems(member));
    internal PropertyNumericRange? CollectionNumericRange(ProceduralCollectionItem item)
        => CollectionItemPresenter(item) is { } presenter ? NumericPresentation?.NumericRange(presenter) : null;
    internal IReadOnlyList<PropertyNumericRange?> CollectionAxisRanges(ProceduralCollectionItem item)
        => CollectionItemPresenter(item) is { } presenter ? NumericPresentation!.NumericAxisRanges(presenter, VectorPropertyNumericMetadata.Create(item.Type)) : VectorPropertyNumericMetadata.Create(item.Type);
    internal bool CollectionAxisReadOnly(ProceduralCollectionItem item, int axis)
        => CollectionItemPresenter(item) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    internal object NormalizeNativeClipboardItem(IObjectNode node, NodeIndex index, object value)
    {
        if (NumericPresentation?.ItemPresenter(node, index) is not { } presenter) return value;
        var previous = (node.Indices ?? []).Contains(index) ? node.Retrieve(index) : null;
        var normalized = NativeNumericAxes.Normalize(PropertyValueValidation.Normalize(value, NumericPresentation.NumericRange(presenter)),
            previous, NumericPresentation.NumericAxisRanges(presenter, VectorPropertyNumericMetadata.Create(presenter.Type)))!;
        NativeNumericAxes.AssertEditable(presenter, previous, normalized); return normalized;
    }
    internal void UpdateCollectionItem(ProceduralCollectionItem item, object? expected, object? value)
    {
        if (!CanEditCollectionItem(item) || !ContentAssetReference.AreEqual(item.Value, expected)) throw new InvalidOperationException("The procedural collection item changed.");
        value = NormalizeCollectionItem(item, value);
        if (value != null && !ContentAssetReference.Supports(item.Type)) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        ApplyInheritedAssetMutation(() => item.Context.Node.Update(value, item.Index), "Edit item: " + item.Context.Origin.Label);
    }
    private object? NormalizeCollectionItem(ProceduralCollectionItem item, object? value)
    {
        if (value == null ? !item.AllowsNull : !item.DeclaredType.IsInstanceOfType(value)) throw new ArgumentException("The collection value is incompatible.");
        if (ContentAssetReference.Supports(item.Type)) ContentAssetReference.Validate(item.Type, value, item.AllowsNull);
        if (CollectionItemPresenter(item) is { } presenter)
        {
            value = PropertyValueValidation.Normalize(value, NumericPresentation?.NumericRange(presenter));
            var ranges = NumericPresentation!.NumericAxisRanges(presenter, VectorPropertyNumericMetadata.Create(item.Type));
            value = NativeNumericAxes.Normalize(value, item.Value, ranges);
            NativeNumericAxes.AssertEditable(presenter, item.Value, value);
        }
        return value;
    }
    internal void AddCollectionItem(ProceduralCollectionContext context, object? value, object? key = null)
    {
        if (!CanAddCollection(context)) throw new InvalidOperationException("The procedural collection cannot add items.");
        if (value == null ? context.Rules?.NotNullItems == true || context.ElementType.IsValueType && Nullable.GetUnderlyingType(context.ElementType) == null
            : !context.ElementType.IsInstanceOfType(value)) throw new ArgumentException("The collection value is incompatible.");
        if (context.Node.Descriptor is DictionaryDescriptor dictionary
            && (key == null || !dictionary.KeyType.IsInstanceOfType(key) || dictionary.ContainsKey(context.Node.Retrieve(), key)))
            throw new ArgumentException("The dictionary key is incompatible or already present.");
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        ApplyInheritedAssetMutation(() => context.Node.Add(value!, context.Node.Descriptor is DictionaryDescriptor ? new NodeIndex(key)
            : context.Node.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((context.Node.Indices ?? []).Count())), "Add item: " + context.Origin.Label);
    }
    internal void RemoveCollectionItem(ProceduralCollectionItem item)
    {
        if (!CanRemoveCollectionItem(item)) throw new InvalidOperationException("The procedural collection item cannot be removed.");
        ApplyInheritedAssetMutation(() => item.Context.Node.Remove(item.Value!, item.Index), "Remove item: " + item.Context.Origin.Label);
    }
    internal bool CanMoveCollectionItem(ProceduralCollectionItem item)
        => CanRemoveCollectionItem(item) && CanAddCollection(item.Context) && item.Context.Rules?.CanReorderItems == true
            && item.Context.Node.Descriptor is CollectionDescriptor { HasInsert: true };
    internal void MoveCollectionItem(ProceduralCollectionItem item, int destination)
    {
        if (!CanMoveCollectionItem(item) || destination < 0 || destination >= (item.Context.Node.Indices ?? []).Count())
            throw new InvalidOperationException("The procedural item cannot be moved.");
        ApplyInheritedAssetMutation(() => RelocateCollectionItem(item, new NodeIndex(destination), false), "Move item: " + item.Context.Origin.Label);
    }
    internal bool CanRenameCollectionKey(ProceduralCollectionItem item)
        => CanEditCollectionItem(item) && item.Context.Rules?.ReadOnly != true
            && NativePropertyClipboard.HasAdd(item.Context.Node) && NativePropertyClipboard.HasRemove(item.Context.Node)
            && item.Context.Node.Descriptor is DictionaryDescriptor dictionary
            && NativeDictionaryKey.SupportsText(dictionary.KeyType);
    internal void RenameCollectionKey(ProceduralCollectionItem item, object? expectedKey, string text)
    {
        if (!CanRenameCollectionKey(item) || !Equals(item.Index.Value, expectedKey)) throw new InvalidOperationException("The dictionary key changed.");
        var dictionary = (DictionaryDescriptor)item.Context.Node.Descriptor;
        var key = NativeDictionaryKey.Parse(dictionary.KeyType, text);
        if (Equals(key, expectedKey)) return;
        if (dictionary.ContainsKey(item.Context.Node.Retrieve(), key)) return;
        ApplyInheritedAssetMutation(() => RelocateCollectionItem(item, new NodeIndex(key), true), "Rename key: " + item.Context.Origin.Label);
    }
    private static void RelocateCollectionItem(ProceduralCollectionItem item, NodeIndex destination, bool rename)
    {
        var node = item.Context.Node; var value = item.Value;
        if (item.Id is not { } id || node.BaseNode != null) { node.Remove(value!, item.Index); node.Add(value!, destination); return; }
        var itemOverride = node.GetItemOverride(item.Index); var keyOverride = node.GetKeyOverride(item.Index);
        node.RemoveAndDiscard(value!, item.Index, id); node.Restore(value!, destination, id);
        node.RestoreItemOverrides(destination, itemOverride, rename ? keyOverride | OverrideType.New : keyOverride);
    }
    internal bool CanResetCollectionItem(ProceduralCollectionItem item)
        => CanEditCollectionItem(item) && item.Id is { } id && item.Context.Node.BaseNode is IAssetObjectNode source && source.HasId(id);
    internal void ResetCollectionItem(ProceduralCollectionItem item)
    {
        if (!CanResetCollectionItem(item)) throw new InvalidOperationException("This procedural item has no editable base value.");
        ApplyInheritedAssetMutation(() => item.Context.Node.ResetOverrideRecursively(item.Index), "Reset to base value");
    }
}
