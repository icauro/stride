using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawUIRegisteredPresenterCommands(UIDocument source, IReadOnlyList<Guid> elementIds,
        Func<bool> currentTargets, Func<EditorPresenterTarget?[]> acquire)
    {
        var ids = elementIds.ToArray(); var providerVersion = source.SessionHistory?.Extensions?.Version;
        bool Current() => ReferenceEquals(ownedUIEditor?.Document, source) && ownedUIEditor.Selection.SequenceEqual(ids)
            && providerVersion == source.SessionHistory?.Extensions?.Version
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && ids.All(id => source.Find(id) != null) && currentTargets();
        DrawNativePresenterCommands(source, Current, acquire, () => new EditorAssetCommandMutation(source.BeginExtensionCommandMutation()));
    }
    private void DrawUICollectionProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, IReadOnlyList<UIPropertyCollection> targets)
    {
        var collections = targets.DistinctBy(collection => collection.Collection).ToArray();
        DrawUIRegisteredPresenterCommands(source, ids, () => collections.Length != 0 && collections.All(source.IsUICollectionCommandCurrent),
            () => AcquireHierarchyFactoryTargets(collections.Length, index => source.AcquireExtensionFactoryTarget(new NodeAccessor(collections[index].Collection, NodeIndex.Empty))));
    }
    private void DrawUICollectionItemProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, IReadOnlyList<UIPropertyCollectionItem> targets)
    {
        var items = targets.DistinctBy(item => (item.Owner.Collection, item.Identity)).ToArray();
        DrawUIRegisteredPresenterCommands(source, ids, () => items.Length != 0 && items.All(source.IsUICollectionItemCommandCurrent),
            () => AcquireHierarchyFactoryTargets(items.Length, index => source.AcquireExtensionFactoryTarget(new NodeAccessor(items[index].Owner.Collection, items[index].Index))));
    }
    private void DrawUIAttachedProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key)
        => DrawUIRegisteredPresenterCommands(source, ids, () => ids.All(id => source.HasDependencyProperty(id, key)),
            () => AcquireHierarchyFactoryTargets(ids.Count, index => source.AcquireUIAttachedCommandTarget(ids[index], key)));
    private void DrawUIVirtualProviderCommands(UIDocument source, IReadOnlyList<VirtualNodePresenter> targets)
    {
        var presenters = targets.ToArray(); var ids = ownedUIEditor?.Selection.ToArray() ?? [];
        DrawUIRegisteredPresenterCommands(source, ids,
            () => presenters.Length != 0 && presenters.All(presenter => source.CombinedExtensionVirtualProperties().Any(group => group.Any(current => ReferenceEquals(current, presenter)))),
            () => [source.AcquireCombinedVirtualTarget(presenters)]);
    }
    private void DrawUIMemberProviderCommands(UIDocument source, IReadOnlyList<IMemberNode> targets)
    {
        var members = targets.Distinct().ToArray(); var ids = ownedUIEditor?.Selection.ToArray() ?? [];
        DrawUIRegisteredPresenterCommands(source, ids, () => members.Length != 0 && members.All(source.CanCopyAssetProperty),
            () => AcquireHierarchyFactoryTargets(members.Length, index => source.AcquireExtensionCommandTarget(members[index])));
    }
}
