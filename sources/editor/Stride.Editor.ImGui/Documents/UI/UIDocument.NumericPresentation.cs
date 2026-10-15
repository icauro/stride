using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal override IReadOnlyList<PropertyNumericRange?> ExtensionAxisNumericRanges(IMemberNode member) => UIAxisNumericRanges(member);
    internal override bool ExtensionNumericAxisReadOnly(IMemberNode member, int axis) => IsUIAxisReadOnly(member, axis);
    internal IReadOnlyList<PropertyNumericRange?> UIAxisNumericRanges(IMemberNode member)
    {
        var fallback = VectorPropertyNumericMetadata.Create(member);
        var owner = ElementPresentation(member);
        return owner?.Find(member) is { } presenter ? owner.NumericAxisRanges(presenter, fallback) : fallback;
    }
    internal IReadOnlyList<PropertyNumericRange?> UIAxisNumericRanges(VirtualNodePresenter presenter)
    {
        var fallback = VectorPropertyNumericMetadata.Create(Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type);
        return elementPresentations.Values.FirstOrDefault(owner => owner.VirtualRows.Contains(presenter))?.NumericAxisRanges(presenter, fallback) ?? fallback;
    }
    internal IReadOnlyList<PropertyNumericRange?> UIAxisNumericRanges(UIPropertyCollectionItem item)
    {
        var fallback = VectorPropertyNumericMetadata.Create(Nullable.GetUnderlyingType(item.Owner.ElementType) ?? item.Owner.ElementType);
        foreach (var owner in elementPresentations.Values)
            if (owner.ItemPresenter(item.Owner.Collection, item.Index) is { } presenter) return owner.NumericAxisRanges(presenter, fallback);
        return fallback;
    }
    internal IReadOnlyList<PropertyNumericRange?> UIAxisNumericRanges(IObjectNode collection, NodeIndex index, Type type)
    {
        var fallback = VectorPropertyNumericMetadata.Create(Nullable.GetUnderlyingType(type) ?? type);
        foreach (var owner in elementPresentations.Values)
            if (owner.ItemPresenter(collection, index) is { } presenter) return owner.NumericAxisRanges(presenter, fallback);
        return fallback;
    }
    internal PropertyNumericRange? UIItemNumericRange(IObjectNode collection, NodeIndex index)
    {
        foreach (var owner in elementPresentations.Values)
            if (owner.ItemPresenter(collection, index) is { } presenter) return owner.NumericRange(presenter);
        return null;
    }
    internal IReadOnlyList<PropertyNumericRange?> UIAttachedAxisNumericRanges(Guid id, Stride.Core.PropertyKey key)
        => UIAxisNumericRanges(DependencyPropertyNode(id), new NodeIndex(key), key.PropertyType);
    internal PropertyNumericRange? UIAttachedNumericRange(Guid id, Stride.Core.PropertyKey key)
        => UIItemNumericRange(DependencyPropertyNode(id), new NodeIndex(key));
    internal bool IsUIAxisReadOnly(IMemberNode member, int axis)
        => ElementPresentation(member)?.Find(member) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    internal bool IsUIAxisReadOnly(VirtualNodePresenter presenter, int axis) => NativeNumericAxes.IsReadOnly(presenter, axis);
    internal bool IsUIAxisReadOnly(UIPropertyCollectionItem item, int axis)
        => FindExtensionItemPresenter(item.Owner.Collection, item.Index) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    internal bool IsUIAttachedAxisReadOnly(Guid id, Stride.Core.PropertyKey key, int axis)
        => FindExtensionItemPresenter(DependencyPropertyNode(id), new NodeIndex(key)) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    internal void AssertUIAxesEditable(IMemberNode member, object? previous, object? proposed)
    { if (ElementPresentation(member)?.Find(member) is { } presenter) NativeNumericAxes.AssertEditable(presenter, previous, proposed); }
    internal void AssertUIItemAxesEditable(IObjectNode collection, NodeIndex index, object? previous, object? proposed)
    { if (FindExtensionItemPresenter(collection, index) is { } presenter) NativeNumericAxes.AssertEditable(presenter, previous, proposed); }
}
