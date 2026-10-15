namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private sealed record UIValueTarget(UIDocument Source, object Identity, Func<bool> CanEdit,
        Func<object?[]> Read, Action<Func<object?, object?>> Transform, Func<IDisposable>? Acquire = null,
        Func<int, PropertyNumericRange?>? AxisRange = null, Func<int, bool>? CanEditAxis = null);
    private UIValueTarget MemberValueTarget(UIDocument source, Guid[] ids, string path)
    {
        var fields = ids.Select(id => source.Properties(id).Single(field => field.Name == path)).ToArray();
        return new(source, fields[0].Node,
            () => fields.Select((field, index) => source.Properties(ids[index]).Any(current => current.Name == path
                && ReferenceEquals(current.Node, field.Node) && source.CanEdit(current))).All(current => current),
            () => fields.Select(field => field.Node.Retrieve()).Concat(fields.SelectMany((field, index) =>
                new object?[] { field.Node, field.Node.Parent.Retrieve(), source.Find(ids[index]), source.Find(ids[index])?.VisualParent })).ToArray(),
            transform => source.TransformProperties(ids, path, transform),
            () => source.AcquireUIPropertyValueTarget(fields.Select(field => field.Node).ToArray(), ids),
            AxisRange: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(fields[0].Type), fields.Select(field => source.UIAxisNumericRanges(field.Node).ElementAtOrDefault(axis))),
            CanEditAxis: axis => fields.All(field => !source.IsUIAxisReadOnly(field.Node, axis)));
    }
    private UIValueTarget CollectionValueTarget(UIDocument source, UIPropertyCollectionItem[] items)
        => new(source, items[0].Owner.Collection, () => items.All(source.CanEditCollectionItem),
            () => items.Select(item => item.Value).Concat(items.SelectMany(item =>
                new object?[] { item.Owner.Member, item.Owner.Collection, item.Index, item.Id, item.Owner.ElementId })).ToArray(),
            transform => source.TransformCollectionItems(items, transform),
            () => source.AcquireUICollectionValueTarget(items),
            AxisRange: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(items[0].Owner.ElementType), items.Select(item => source.UIAxisNumericRanges(item).ElementAtOrDefault(axis))),
            CanEditAxis: axis => items.All(item => !source.IsUIAxisReadOnly(item, axis)));
    private UIValueTarget AttachedValueTarget(UIDocument source, Guid[] ids, Stride.Core.PropertyKey key)
        => new(source, source, () => ids.All(id => source.CanChangeDependencyProperty(id, key)),
            () => ids.Select(id => source.ReadDependencyProperty(id, key)).Concat(ids.SelectMany(id =>
                new object?[] { source.Find(id), source.Find(id)?.VisualParent, key })).ToArray(),
            transform => source.TransformDependencyPropertyValues(ids, key, transform),
            () => source.AcquireUIAttachedValueTarget(ids, key),
            AxisRange: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(key.PropertyType), ids.Select(id => source.UIAttachedAxisNumericRanges(id, key).ElementAtOrDefault(axis))),
            CanEditAxis: axis => ids.All(id => !source.IsUIAttachedAxisReadOnly(id, key, axis)));
    private UIValueTarget VirtualValueTarget(UIDocument source,
        IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter> presenters)
        => new(source, presenters[0], () => presenters.All(source.CanEditExtensionVirtualProperty),
            () => presenters.Select(source.ReadExtensionVirtualProperty).ToArray(),
            transform => source.TransformExtensionVirtualProperties(presenters, transform),
            () => source.AcquireCombinedVirtualTarget(presenters),
            AxisRange: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(presenters[0].Type), presenters.Select(presenter => source.UIAxisNumericRanges(presenter).ElementAtOrDefault(axis))),
            CanEditAxis: axis => presenters.All(presenter => !source.IsUIAxisReadOnly(presenter, axis)));
}
