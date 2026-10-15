using Stride.Core.Assets;
using Stride.Core;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal override IEnumerable<AbstractNodeEntry>? ExtensionAbstractEntries(IMemberNode member) => ElementPresentation(member)?.AbstractEntries(member);
    internal override IEnumerable<AbstractNodeEntry>? ExtensionCollectionAbstractEntries(IObjectNode collection)
    {
        foreach (var presentation in elementPresentations.Values)
            if (EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, collection) is { } presenter
                && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries)) return entries;
        return null;
    }
    internal override EditorPresenterTarget? AcquireExtensionFactoryTarget(NodeAccessor accessor)
    {
        if (accessor.Node is IMemberNode member) return AcquireExtensionCommandTarget(member);
        if (accessor.Node is IObjectNode collection)
            foreach (var presentation in elementPresentations.Values)
            {
                var presenter = accessor.Index.IsEmpty ? EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, collection)
                    : EditorPresenterItemMetadata.Find(presentation.RootPresenter, collection, accessor.Index);
                if (presenter != null) return presentation.AcquireTarget(presenter);
            }
        var virtualRow = ExtensionVirtualProperties.FirstOrDefault(presenter =>
        { var current = presenter.GetNodeAccessor(); return ReferenceEquals(current.Node, accessor.Node) && current.Index == accessor.Index; });
        return virtualRow == null ? null : AcquireExtensionVirtualTarget(virtualRow);
    }
    internal IReadOnlyList<AbstractNodeEntry>? CommonUIVirtualEntries(IReadOnlyList<VirtualNodePresenter> presenters)
    {
        var entries = presenters.Select(ExtensionAbstractEntries).Where(entries => entries != null).Select(entries => (IReadOnlyList<AbstractNodeEntry>)entries!.ToArray()).ToArray();
        return entries.Length == 0 ? null : NativeAbstractEntries.Combine(entries);
    }
    internal IReadOnlyList<AbstractNodeEntry>? CommonUIAttachedEntries(IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key)
    {
        var entries = ids.Select(id => ExtensionAbstractEntries(DependencyPropertyNode(id), new NodeIndex(key)))
            .Where(entries => entries != null).Select(entries => (IReadOnlyList<AbstractNodeEntry>)entries!.ToArray()).ToArray();
        return entries.Length == 0 ? null : NativeAbstractEntries.Combine(entries);
    }
    internal void ChangeUIVirtualEntry(IReadOnlyList<VirtualNodePresenter> presenters, AbstractNodeEntry entry)
    {
        var entries = CommonUIVirtualEntries(presenters);
        if (entries == null || !NativeAbstractEntries.Contains(entries, entry) || presenters.Any(presenter => !NativeAbstractEntries.Accepts(ExtensionAbstractEntries(presenter), entry))) throw new InvalidOperationException("The native UI entry is no longer available.");
        TransformExtensionVirtualProperties(presenters, current => entry.IsMatchingValue(current!) ? current : entry.GenerateValue(current!));
    }
    internal void ChangeUIAttachedEntry(IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key, AbstractNodeEntry entry)
    {
        var entries = CommonUIAttachedEntries(ids, key);
        if (entries == null || !NativeAbstractEntries.Contains(entries, entry) || ids.Any(id => !NativeAbstractEntries.Accepts(ExtensionAbstractEntries(DependencyPropertyNode(id), new NodeIndex(key)), entry))) throw new InvalidOperationException("The native attached entry is no longer available.");
        TransformDependencyPropertyValues(ids, key, current => entry.IsMatchingValue(current!) ? current : entry.GenerateValue(current!));
    }
    internal bool CanSelectUIVirtualEntry(IReadOnlyList<VirtualNodePresenter> presenters, AbstractNodeEntry entry)
        => presenters.All(presenter => NativeAbstractEntries.Accepts(ExtensionAbstractEntries(presenter), entry));
    internal bool CanSelectUIAttachedEntry(IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key, AbstractNodeEntry entry)
        => ids.All(id => NativeAbstractEntries.Accepts(ExtensionAbstractEntries(DependencyPropertyNode(id), new NodeIndex(key)), entry));
    internal bool CanSelectUICollectionItemEntry(IReadOnlyList<UIPropertyCollectionItem> items, AbstractNodeEntry entry)
        => items.All(item => NativeAbstractEntries.Accepts(ExtensionAbstractEntries(item.Owner.Collection, item.Index) ?? ExtensionCollectionAbstractEntries(item.Owner.Collection), entry));
    private EditorPresenterTarget? AcquireFactoryTargets(IEnumerable<NodeAccessor> accessors)
    {
        var targets = new List<EditorPresenterTarget>();
        try
        {
            foreach (var accessor in accessors)
            {
                var target = AcquireExtensionFactoryTarget(accessor);
                if (target == null) { foreach (var owned in targets) owned.Dispose(); return null; }
                targets.Add(target);
            }
            return targets.Count == 0 ? null : new(targets);
        }
        catch { foreach (var target in targets) target.Dispose(); throw; }
    }
    internal EditorPresenterTarget? AcquireAttachedFactoryTarget(IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key)
        => AcquireFactoryTargets(ids.Select(id => new NodeAccessor(DependencyPropertyNode(id), new NodeIndex(key))));
    internal EditorPresenterTarget AcquireUIPropertyValueTarget(IReadOnlyList<IMemberNode> members, IReadOnlyList<Guid> ids)
        => AcquireFactoryTargets(members.Select(member => new NodeAccessor(member, NodeIndex.Empty)))
            ?? AcquireUIValuePresenterRoots(ids);
    internal EditorPresenterTarget? AcquireUICollectionFactoryTarget(IReadOnlyList<UIPropertyCollectionItem> items)
        => AcquireFactoryTargets(items.Select(item => new NodeAccessor(item.Owner.Collection, item.Index)));
    internal EditorPresenterTarget? AcquireUICollectionAddTarget(IReadOnlyList<UIPropertyCollection> collections)
        => AcquireFactoryTargets(collections.Select(collection => new NodeAccessor(collection.Collection, NodeIndex.Empty)));
    internal IReadOnlyList<AbstractNodeEntry>? CommonUICollectionItemEntries(IReadOnlyList<UIPropertyCollectionItem> items)
    {
        var entries = items.Select(item => ExtensionAbstractEntries(item.Owner.Collection, item.Index) ?? ExtensionCollectionAbstractEntries(item.Owner.Collection))
            .Where(entries => entries != null).Select(entries => (IReadOnlyList<AbstractNodeEntry>)entries!.ToArray()).ToArray();
        return entries.Length == 0 ? null : NativeAbstractEntries.Combine(entries);
    }
    internal void ChangeUICollectionItemEntry(IReadOnlyList<UIPropertyCollectionItem> items, AbstractNodeEntry entry)
    {
        var entries = CommonUICollectionItemEntries(items);
        if (entries == null || !NativeAbstractEntries.Contains(entries, entry) || !CanSelectUICollectionItemEntry(items, entry)) throw new InvalidOperationException("The native collection entry is no longer available.");
        TransformCollectionItems(items, current => entry.IsMatchingValue(current!) ? current : entry.GenerateValue(current!));
    }
    internal IReadOnlyList<AbstractNodeEntry> UIPropertyEntries(Guid id, string path)
    {
        var field = Properties(id).Single(field => field.Name == path);
        return NativeAbstractEntries.Available(ExtensionAbstractEntries(field.Node), field.ObjectTypes, field.AllowsNull);
    }
    internal IReadOnlyList<AbstractNodeEntry> CommonUIPropertyEntries(IReadOnlyList<Guid> ids, string path)
        => NativeAbstractEntries.Combine(ids.Select(id => UIPropertyEntries(id, path)).ToArray());
    internal void ChangeUIPropertyEntry(IReadOnlyList<Guid> ids, string path, AbstractNodeEntry entry)
    {
        EndEdit();
        var fields = ids.Select(id => Properties(id).Single(field => field.Name == path)).ToArray();
        if (fields.Length == 0 || fields.Any(field => !CanEdit(field)) || !NativeAbstractEntries.Contains(CommonUIPropertyEntries(ids, path), entry) || ids.Any(id => !NativeAbstractEntries.Contains(UIPropertyEntries(id, path), entry)))
            throw new InvalidOperationException("The native UI property entry is no longer available.");
        var changes = fields.Where(field => !entry.IsMatchingValue(field.Node.Retrieve()!)).Select(field =>
        {
            var value = entry.GenerateValue(field.Node.Retrieve()!);
            if (value == null ? !field.AllowsNull : !field.Type.IsInstanceOfType(value)) throw new ArgumentException("The UI property entry has an incompatible value.");
            if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
            var removed = field.Node.Target == null ? new HashSet<Guid>() : IdentifiableObjectCollector.Collect(AuthoringGraph.Definition, field.Node.Target).Keys.ToHashSet();
            if (field.Node.Retrieve() is IIdentifiable identifiable) removed.Add(identifiable.Id);
            if (value != null)
            {
                var retained = AuthoringGraph.Container.NodeContainer.GetOrCreateNode(value)!;
                removed.ExceptWith(IdentifiableObjectCollector.Collect(AuthoringGraph.Definition, retained).Keys);
                if (value is IIdentifiable replacement) removed.Remove(replacement.Id);
            }
            return (Field: field, Value: value, Removed: removed);
        }).ToArray();
        Mutate(() =>
        {
            foreach (var change in changes) change.Field.Node.Update(change.Value);
            AuthoringGraph.ClearReferencesToObjects(changes.SelectMany(change => change.Removed).Distinct());
        }, "Edit " + fields[0].Label);
    }
    internal IReadOnlyList<AbstractNodeEntry> UICollectionEntries(UIPropertyCollection collection)
        => ExtensionCollectionAbstractEntries(collection.Collection)?.ToArray() ?? [];
    internal bool HasUICollectionFactoryEntries(UIPropertyCollection collection)
        => ExtensionCollectionAbstractEntries(collection.Collection) != null;
    internal bool CanDefaultUICollectionAdd(UIPropertyCollection collection)
        => CanAddCollectionItem(collection) && collection.Collection.Descriptor is Stride.Core.Reflection.CollectionDescriptor
            && !HasUICollectionFactoryEntries(collection);
    internal void AddDefaultUICollectionItem(UIPropertyCollection collection)
    {
        EndEdit();
        if (!CanDefaultUICollectionAdd(collection)) throw new InvalidOperationException("The native default UI collection command changed.");
        var instance = Stride.Core.Reflection.ObjectFactoryRegistry.NewInstance(collection.ElementType);
        var value = AssetCollectionCommandRules.IsReferenceType(collection.ElementType) || AuthoringGraph.Definition.IsObjectReference(collection.Accessor, instance)
            ? null : instance;
        ValidateCollectionValue(collection, value);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        Mutate(() =>
        {
            if (!CanDefaultUICollectionAdd(collection)) throw new InvalidOperationException("The native default UI collection target changed.");
            var index = collection.Collection.Descriptor is Stride.Core.Reflection.SetDescriptor ? new NodeIndex(value)
                : new NodeIndex((collection.Collection.Indices ?? []).Count());
            collection.Collection.Add(value, index);
        }, "Add UI collection item");
    }
    internal void AddUICollectionEntry(UIPropertyCollection collection, AbstractNodeEntry entry, string? key)
    {
        EndEdit();
        if (!CanAddCollectionItem(collection) || !NativeAbstractEntries.Contains(UICollectionEntries(collection), entry))
            throw new InvalidOperationException("The native UI collection entry is no longer available.");
        AddCollectionItem(collection, null, key, entry.GenerateValue(null!));
    }
}
