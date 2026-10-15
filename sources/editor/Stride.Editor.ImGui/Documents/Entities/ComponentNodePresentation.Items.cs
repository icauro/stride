using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using System.Runtime.CompilerServices;

namespace Stride.Editor.ImGui;

internal sealed partial class ComponentNodePresentation
{
    private sealed record ItemNumericMetadata(object? Minimum, object? Maximum, object? Small, object? Large, object? Places, PropertyNumericRange Range);
    private readonly ConditionalWeakTable<INodePresenter, ItemNumericMetadata> itemNumericMetadata = new();
    private sealed record ItemAxesMetadata(Type Type, IReadOnlyList<PropertyNumericRange?> Fallback, IReadOnlyList<PropertyNumericRange?> Ranges);
    private readonly ConditionalWeakTable<INodePresenter, ItemAxesMetadata> itemAxesMetadata = new();
    private bool IsOwnedCollection(IObjectNode requested)
        => requested.IsEnumerable && IsOwnedCollectionObject(requested);
    private bool IsOwnedCollectionObject(IObjectNode requested)
    {
        var pending = new Stack<(IObjectNode Node, int Depth)>();
        if (authoredRoot.IsEnumerable) pending.Push((authoredRoot, 0));
        foreach (var member in members)
            if (member.Target is { IsEnumerable: true } collection) pending.Push((collection, 0));
        var visited = new HashSet<IObjectNode>();
        while (pending.Count != 0)
        {
            var (node, depth) = pending.Pop();
            if (!visited.Add(node) || UnknownAssetData.IsProxyNode(node)) continue;
            if (ReferenceEquals(node, requested)) return true;
            if (depth >= 16) continue;
            if (node.IsEnumerable)
            {
                if (node.ItemReferences != null)
                    foreach (var index in node.Indices ?? [])
                        if (isOwnedItem?.Invoke(node, index) != false && node.IndexedTarget(index) is { } nested)
                            pending.Push((nested, depth + 1));
            }
            else foreach (var member in node.Members)
                if (isOwnedMember?.Invoke(member) != false && member.Target is { } target) pending.Push((target, depth + 1));
        }
        return false;
    }
    internal INodePresenter? FindItem(IObjectNode collection, NodeIndex index)
    {
        RebuildMap();
        if (disposed || disposeRequested || !IsOwnedCollection(collection) || !(collection.Indices ?? []).Contains(index)) return null;
        return EditorPresenterItemMetadata.Find(root, collection, index);
    }
    internal bool IsItemVisible(IObjectNode collection, NodeIndex index)
        => FindItem(collection, index) is not { } item || EditorPresenterItemMetadata.IsVisible(item);
    internal bool IsAccessorReadOnly(IGraphNode node, NodeIndex index, IMemberNode? collectionMutationMember = null)
    {
        RebuildMap();
        var presenter = node is IMemberNode member ? presenters.GetValueOrDefault(member)
            : node is IObjectNode collection ? index.IsEmpty ? EditorPresenterItemMetadata.FindCollection(root, collection) : FindItem(collection, index) : null;
        for (; presenter != null; presenter = presenter.Parent)
            if (presenter.IsReadOnly)
            {
                if (collectionMutationMember != null && ReferenceEquals(presenter, presenters.GetValueOrDefault(collectionMutationMember))
                    && presenter is MemberNodePresenter { IsReadOnlyForGetterAssignmentOnly: true }) continue;
                return true;
            }
        return false;
    }
    internal string? ItemLabel(IObjectNode collection, NodeIndex index)
        => FindItem(collection, index) is { } item && item.DisplayName != item.Name ? item.DisplayName : null;
    internal IEnumerable<AbstractNodeEntry>? ItemEntries(IObjectNode collection, NodeIndex index)
        => FindItem(collection, index) is { } item && item.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries : null;
    internal IEnumerable<AbstractNodeEntry>? CollectionEntries(IObjectNode collection)
    {
        RebuildMap();
        return IsOwnedCollection(collection) && EditorPresenterItemMetadata.FindCollection(root, collection) is { } presenter
            && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries : null;
    }
    internal PropertyNumericRange? ItemNumericRange(IObjectNode collection, NodeIndex index, Type type)
        => FindItem(collection, index) is { } item ? ReadItemNumericRange(item, type) : null;
    private PropertyNumericRange? ReadItemNumericRange(INodePresenter presenter, Type type)
    {
        if (!AssetSettingNumericMetadata.Supports(type)) return null;
        var properties = presenter.AttachedProperties;
        properties.TryGetValue(NumericData.MinimumKey, out var minimum); properties.TryGetValue(NumericData.MaximumKey, out var maximum);
        properties.TryGetValue(NumericData.SmallStepKey, out var small); properties.TryGetValue(NumericData.LargeStepKey, out var large);
        properties.TryGetValue(NumericData.DecimalPlacesKey, out var places);
        if (minimum == null && maximum == null && small == null && large == null && places == null) return null;
        if (itemNumericMetadata.TryGetValue(presenter, out var cached) && Equals(cached.Minimum, minimum) && Equals(cached.Maximum, maximum)
            && Equals(cached.Small, small) && Equals(cached.Large, large) && Equals(cached.Places, places)) return cached.Range;
        var range = new PropertyNumericRange(new PropertyNumericMetadata(minimum == null ? null : Convert.ToDouble(minimum),
            maximum == null ? null : Convert.ToDouble(maximum), small == null ? null : Convert.ToDouble(small),
            large == null ? null : Convert.ToDouble(large), places == null ? null : Convert.ToInt32(places)), type, roundDecimalPlaces: places != null);
        itemNumericMetadata.Remove(presenter); itemNumericMetadata.Add(presenter, new(minimum, maximum, small, large, places, range));
        return range;
    }
    internal IReadOnlyList<PropertyNumericRange?> ItemAxisNumericRanges(IObjectNode collection, NodeIndex index, Type type, IReadOnlyList<PropertyNumericRange?> fallback)
        => FindItem(collection, index) is { } item ? ReadAxisNumericRanges(item, type, fallback) : fallback;
    internal IReadOnlyList<PropertyNumericRange?> MemberAxisNumericRanges(ComponentField field, IReadOnlyList<PropertyNumericRange?> fallback)
        => Find(field) is { } presenter ? ReadAxisNumericRanges(presenter, field.Type, fallback) : fallback;
    internal bool IsMemberAxisReadOnly(ComponentField field, int axis)
        => Find(field) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    internal bool IsItemAxisReadOnly(IObjectNode collection, NodeIndex index, int axis)
        => FindItem(collection, index) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    internal void AssertMemberAxesEditable(ComponentField field, object? previous, object? proposed)
    { if (Find(field) is { } presenter) NativeNumericAxes.AssertEditable(presenter, previous, proposed); }
    internal void AssertItemAxesEditable(IObjectNode collection, NodeIndex index, object? previous, object? proposed)
    { if (FindItem(collection, index) is { } presenter) NativeNumericAxes.AssertEditable(presenter, previous, proposed); }
    private IReadOnlyList<PropertyNumericRange?> ReadAxisNumericRanges(INodePresenter item, Type type, IReadOnlyList<PropertyNumericRange?> fallback)
    {
        var names = NativeNumericAxes.Names(type);
        if (names.Count == 0) return fallback;
        itemAxesMetadata.TryGetValue(item, out var cached);
        var previous = cached?.Type == type && ReferenceEquals(cached.Fallback, fallback) ? cached.Ranges
            : names.Count == fallback.Count ? fallback : Enumerable.Range(0, names.Count).Select(axis => fallback.ElementAtOrDefault(axis)).ToArray();
        PropertyNumericRange?[]? changed = null;
        for (var axis = 0; axis < names.Count; axis++)
        {
            var child = item.Children.FirstOrDefault(child => child.Name == names[axis]);
            var range = child == null ? fallback.ElementAtOrDefault(axis) : ReadItemNumericRange(child, Nullable.GetUnderlyingType(child.Type) ?? child.Type) ?? fallback.ElementAtOrDefault(axis);
            if (!ReferenceEquals(range, previous[axis])) { changed ??= previous.ToArray(); changed[axis] = range; }
        }
        if (changed == null)
        {
            if (cached == null) itemAxesMetadata.Add(item, new(type, fallback, previous));
            return previous;
        }
        itemAxesMetadata.Remove(item); itemAxesMetadata.Add(item, new(type, fallback, changed));
        return changed;
    }
    internal EditorPresenterTarget? AcquireItemTarget(IObjectNode collection, NodeIndex index, bool collectionRoot = false)
    {
        RebuildMap();
        var presenter = collectionRoot ? EditorPresenterItemMetadata.FindCollection(root, collection) : FindItem(collection, index);
        if (disposed || disposeRequested || !IsOwnedCollection(collection) || presenter == null) return null;
        uses++; var released = false;
        return new(presenter, () => { if (!released) { uses--; released = true; } if (uses == 0 && disposeRequested) Dispose(); });
    }
}
