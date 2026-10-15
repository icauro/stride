using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<INodePresenter, CompositorProperty> compositorNativeProperties = [];
    internal bool IsCompositorNativePresenterCurrent(CompositorProperty property)
        => property.NativePresenter is not { } native || ResolveExtensionPresentation(native)?.OwnsPresenter(native) == true;
    internal EditorPresenterTarget? AcquireCompositorFactoryTarget(CompositorProperty property)
        => property.NativePresenter is { } native ? ResolveExtensionPresentation(native)?.AcquireTarget(native)
            : AcquireExtensionFactoryTarget(property.Accessor);
    internal IEnumerable<Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry>? CompositorPresenterEntries(CompositorProperty property)
        => property.NativePresenter is { } native && native.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries)
            ? entries : null;
    internal CompositorProperty GetCompositorNativeChildProperty(INodePresenter presenter)
    {
        if (presenter is VirtualNodePresenter || ResolveExtensionPresentation(presenter)?.OwnsPresenter(presenter) != true)
            throw new InvalidOperationException("The native compositor child has no current authored presentation owner.");
        if (compositorNativeProperties.TryGetValue(presenter, out var cached)) return cached;
        var accessor = presenter.GetNodeAccessor();
        if (accessor.Node is not IMemberNode && accessor.Node is not IAssetObjectNode)
            throw new InvalidOperationException("The native compositor child has no authored asset accessor.");
        var key = "native/" + NativePresenterCategories.Identity(presenter);
        var property = CreateCompositorProperty(accessor, key, presenter.DisplayName);
        property.NativePresenter = presenter;
        compositorNativeProperties.Add(presenter, property);
        try
        {
        if (accessor.IsItem && accessor.Node is IAssetObjectNode itemOwner)
        {
            if (!itemOwner.TryIndexToId(accessor.Index, out var id))
                throw new InvalidOperationException("The actual compositor item identity is unavailable.");
            property.ItemId = id;
            if (presenter is ItemNodePresenter item && item.OwnerCollection is not VirtualNodePresenter)
                property.ParentCollection = GetCompositorNativeChildProperty(item.OwnerCollection);
        }
        IObjectNode? actualTarget = accessor.Node is IMemberNode member ? member.Target
            : accessor.Node is IObjectNode owner ? accessor.IsItem
                ? owner.ItemReferences != null ? owner.IndexedTarget(accessor.Index) : null
                : owner : null;
        if (actualTarget is IAssetObjectNode collection && collection.Descriptor is CollectionBaseDescriptor
            && ReferenceEquals(collection.Retrieve(), presenter.Value))
        {
            property.Collection = collection;
            property.Rules = presenter is MemberNodePresenter collectionMember
                ? collectionMember.MemberAttributes.OfType<MemberCollectionAttribute>().FirstOrDefault() : null;
            property.Rules ??= collection.Descriptor.Attributes.OfType<MemberCollectionAttribute>().FirstOrDefault();
        }
        var children = ActualChildren(presenter).Where(child => child is not VirtualNodePresenter).Select(GetCompositorNativeChildProperty)
            .OrderBy(child => EditorGame.NativeRowPath(child.NativePresenter!), EditorGame.NativeRowPathComparer.Instance).ToArray();
        property.Items = property.Collection == null ? [] : children.Where(child => child.Accessor.IsItem
            && ReferenceEquals(child.Accessor.Node, property.Collection)).ToArray();
        foreach (var item in property.Items) item.ParentCollection = property;
        property.Children = children.Where(child => !property.Items.Contains(child)).ToArray();
        ApplyCompositorPresentation(property);
        return property;
        }
        catch
        {
            compositorNativeProperties.Remove(presenter); compositorPropertyMembers.Remove(property);
            throw;
        }
    }
    private static IEnumerable<INodePresenter> ActualChildren(INodePresenter parent)
    {
        foreach (var child in parent.Children)
            if (NativePresenterCategories.IsCategory(child))
            {
                foreach (var item in ActualChildren(child)) yield return item;
            }
            else yield return child;
    }
}
