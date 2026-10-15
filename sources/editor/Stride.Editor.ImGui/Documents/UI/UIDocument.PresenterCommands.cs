using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal bool IsUICollectionCommandCurrent(UIPropertyCollection collection)
        => OwnsCollection(collection) && CanCopyAssetProperty(collection.Member);
    internal bool IsUICollectionItemCommandCurrent(UIPropertyCollectionItem item)
        => IsUICollectionCommandCurrent(item.Owner)
            && (item.Id is { } id ? item.Owner.Collection.HasId(id) : (item.Owner.Collection.Indices ?? []).Contains(item.OriginalIndex));
    internal EditorPresenterTarget AcquireUIValuePresenterRoots(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) throw new InvalidOperationException("The UI value has no actual presenter owner.");
        var targets = new List<EditorPresenterTarget>();
        try
        {
            foreach (var id in ids.Distinct())
            {
                if (Find(id) == null || !elementPresentations.TryGetValue(id, out var presentation)
                    || !ReferenceEquals(presentation.AuthoredRoot, ObjectNode(Require(id))))
                    throw new InvalidOperationException("The actual UI value presenter owner is unavailable.");
                targets.Add(presentation.AcquireTarget(presentation.RootPresenter));
            }
            return new(targets);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var target in targets.AsEnumerable().Reverse())
                try { target.Dispose(); }
                catch (Exception cleanup) { RetainFailedPresenterTarget(target); failures.Add(cleanup); }
            if (failures.Count > 1) throw new AggregateException(failures);
            throw;
        }
    }
    internal EditorPresenterTarget AcquireUIAttachedValueTarget(IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key)
        => AcquireAttachedFactoryTarget(ids, key) ?? AcquireUIValuePresenterRoots(ids);
    internal EditorPresenterTarget AcquireUICollectionValueTarget(IReadOnlyList<UIPropertyCollectionItem> items)
        => AcquireUICollectionFactoryTarget(items) ?? AcquireUIValuePresenterRoots(items.Select(item => item.Owner.ElementId).ToArray());
    internal EditorPresenterTarget? AcquireUIAttachedCommandTarget(Guid id, Stride.Core.PropertyKey key)
        => AcquireExtensionFactoryTarget(new NodeAccessor(DependencyPropertyNode(id), new NodeIndex(key)));
}
