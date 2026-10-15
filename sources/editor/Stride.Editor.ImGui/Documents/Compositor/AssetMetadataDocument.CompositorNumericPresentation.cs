using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private INodePresenter? CompositorNumericPresenter(CompositorProperty property)
        => property.NativePresenter ?? (property.Accessor.IsItem ? FindExtensionItemPresenter((IObjectNode)property.Accessor.Node, property.Accessor.Index)
            : property.Accessor.Node is IMemberNode member ? ResolveExtensionPresentation(member)?.Find(member)
                : property.Accessor.Node is IObjectNode owner && ResolveExtensionPresentation(owner) is { } presentation
                    ? EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, owner) : null);
    internal IReadOnlyList<PropertyNumericRange?> CompositorAxisRanges(CompositorProperty property)
    {
        var fallback = property.VectorRanges;
        return CompositorNumericPresenter(property) is { } presenter && ResolveExtensionPresentation(presenter) is { } presentation
            ? presentation.NumericAxisRanges(presenter, fallback) : fallback;
    }
    internal bool CompositorAxisReadOnly(CompositorProperty property, int axis)
        => CompositorNumericPresenter(property) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    internal bool CompositorPresenterReadOnly(CompositorProperty property)
        => CompositorNumericPresenter(property) is { } presenter
            && (EditorPresenterItemMetadata.IsReadOnly(presenter) || EditorPresenterItemMetadata.HasReadOnlyItemAncestor(presenter));
    private void AssertCompositorAxesEditable(CompositorProperty property, object? previous, object? proposed)
    {
        if (CompositorNumericPresenter(property) is { } presenter) NativeNumericAxes.AssertEditable(presenter, previous, proposed);
    }
    internal EditorPresenterTarget? AcquireCompositorPropertyPresenter(CompositorProperty property)
    {
        if (property.NativePresenter is { } native) return ResolveExtensionPresentation(native)?.AcquireTarget(native);
        if (property.Accessor.Node is IObjectNode root && property.Accessor.Index.IsEmpty) return AcquireExtensionFactoryTarget(property.Accessor);
        if (!property.Accessor.IsItem && property.Accessor.Node is IMemberNode member) return AcquireExtensionCommandTarget(member);
        // Retain the actual owning presentation tree, including item presenters.
        for (var owner = property.ParentCollection; owner != null; owner = owner.ParentCollection)
            if (owner.Accessor.Node is IMemberNode collectionMember && AcquireExtensionCommandTarget(collectionMember) is { } target)
                return target;
        return null;
    }
}
