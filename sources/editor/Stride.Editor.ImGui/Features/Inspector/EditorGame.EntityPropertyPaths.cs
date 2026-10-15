using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawEntityPropertyPaths(Type type, IReadOnlyList<EntityHierarchyDocument> owners, IReadOnlyList<Func<object?>> reads,
        Func<bool> canEdit, Action<UPath> assign, bool mixed = false, Func<object?[]>? certificates = null, object? identity = null)
    {
        var history = sessionHistory;
        var currentSelection = new ContentReferencePickerHost(this).CaptureSelectionGuard();
        var states = owners.Select(owner => history?.Registry.Find(owner.Asset.Id)).ToArray();
        var expected = reads.Select(read => read()).ToArray();
        bool Current()
        {
            if (!currentSelection() || !ReferenceEquals(history, sessionHistory) || history == null || owners.Count == 0 || owners.Count != reads.Count
                || owners.Where((owner, index) => states[index] is not { IsPublished: true, IsDisposed: false } state
                    || !ReferenceEquals(state.Document, owner) || !ReferenceEquals(history.Registry.Find(owner.Asset.Id), state)).Any()
                || !canEdit()) return false;
            return true;
        }
        void Assign(UPath path)
        {
            if (!Current()) throw new InvalidOperationException("The selected path properties changed.");
            assign(path);
        }
        object?[] Read() => reads.Select(read => read()).Concat(certificates?.Invoke() ?? []).ToArray();
        DrawTypedPropertyPath(owners[0], type, expected[0], Current, reads[0], Assign, mixed,
            identity ?? owners[0], Read, () => AcquirePropertyStringOwners(owners.Distinct()), reads.Count);
    }
    private void DrawCommonPropertyPath(CommonComponentProperty property, bool inherited)
    {
        var owner = document!;
        DrawEntityPropertyPaths(property.First.Type, property.Fields.Select(_ => owner).ToArray(),
            property.Fields.Select(field => (Func<object?>)(() => field.Node.Retrieve())).ToArray(),
            () => inherited ? CanEditInheritedCommon(property) : owner.CanEditCommonProperty(property),
            path =>
            {
                if (inherited) owner.EditInheritedScalars(property.Fields, property.Fields.Select(_ => (object?)path).ToArray());
                else { owner.UpdateCommonProperty(property, path); owner.EndCommonPropertyEdit(); }
            }, property.IsMixed);
    }
    private void DrawAggregatePropertyPath(CommonComponentProperty property)
        => DrawEntityPropertyPaths(property.First.Type, property.Fields.Select(field => aggregatePropertyOwners[field]).ToArray(),
            property.Fields.Select(field => (Func<object?>)(() => field.Node.Retrieve())).ToArray(),
            () => CanEditAggregateProperty(property), path => ApplyAggregateProperty(property, _ => path), property.IsMixed);
    private void DrawHierarchyItemPaths(CommonComponentProperty property, IReadOnlyList<IHierarchyCollectionValue> items, bool mixed)
    {
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).ToArray();
        DrawEntityPropertyPaths(items[0].Type, owners, items.Select(item => (Func<object?>)(() => item.Value)).ToArray(),
            () => items.Select((item, index) => owners[index].CanSelectHierarchyItemEntry(item)).All(allowed => allowed),
            path => ApplyAggregateMutation(property, field =>
            {
                var index = AggregateFieldIndex(property, field);
                return items[index] is HierarchyCollectionItem nested ? owners[index].PrepareNestedHierarchyUpdate(nested, path)
                    : owners[index].PrepareAggregateCollectionUpdate((CollectionItem)items[index], path);
            }, "Edit item: " + property.First.Label), mixed, () =>
            {
                var baseline = new List<object?>();
                foreach (var item in items)
                {
                    baseline.Add(item.Collection); baseline.Add(item.Index); baseline.Add(item.IdentityKey);
                    if (item is not HierarchyCollectionItem nested) continue;
                    for (var parent = nested.Context.Parent; parent != null; parent = parent.Context.Parent)
                    { baseline.Add(parent.Collection); baseline.Add(parent.Index); baseline.Add(parent.IdentityKey); }
                }
                return baseline.ToArray();
            }, items[0].Collection);
    }
}
