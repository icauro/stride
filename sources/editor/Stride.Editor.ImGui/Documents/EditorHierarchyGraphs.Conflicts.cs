using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorHierarchyGraphs
{
    internal sealed record Conflict(AssetId Target, string Path, Guid Generation,
        IReadOnlyDictionary<AssetId, Guid> Bases, string Message, string Exception);
    private readonly Dictionary<AssetId, Conflict> conflicts = [];
    internal Action<Conflict>? ConflictReported { get; set; }
    internal bool TryGetConflict(AssetId target, out Conflict conflict) => conflicts.TryGetValue(target, out conflict!);
    private void RecordConflict(EntityHierarchyDocument target, Exception error)
    {
        var state = history.Registry.Find(target.Asset.Id);
        if (state == null || !ReferenceEquals(state.Document, target)) return;
        var bases = target.Asset.Hierarchy.Parts.Values.Select(part => part.Base?.BasePartAsset.Id).OfType<AssetId>().Distinct()
            .ToDictionary(id => id, id => history.Registry.Find(id)?.Generation ?? Guid.Empty);
        var failure = new Conflict(target.Asset.Id, target.Path, state.Generation, bases, error.Message, error.ToString());
        conflicts[target.Asset.Id] = failure;
        ConflictReported?.Invoke(failure);
    }
    private void ClearConflict(EntityHierarchyDocument target) => conflicts.Remove(target.Asset.Id);
    internal bool CanRetryConflict(AssetId id)
        => conflicts.TryGetValue(id, out var failure) && history.Registry.Find(id) is { IsPublished: true } state
            && state.Generation == failure.Generation && state.Document is EntityHierarchyDocument { CanSave: true } target
            && registrations.ContainsKey(target) && failure.Bases.All(pair => history.Registry.Find(pair.Key) is { IsPublished: true } source
                && source.Generation == pair.Value);
    internal void RetryConflict(AssetId id)
    {
        history.Registry.EnsureOwnerThread();
        if (!CanRetryConflict(id)) throw new InvalidOperationException("The failed prefab generation changed. Reload the project before retrying.");
        var target = (EntityHierarchyDocument)history.Registry.Find(id)!.Document;
        target.CommitEdits();
        try
        {
            using var transaction = history.CreateTransaction("Update prefab instance");
            using var capture = history.CaptureOperationsAtomic(() =>
            {
                Refresh(target);
                target.ApplyInheritedHierarchyReconciliation(registrations[target].Graph.ReconcileHierarchyWithBase, "Update prefab instance");
            });
            capture.Publish(history, "Update prefab instance");
            // Old event delegates can describe a compensated edit. Recover from the
            // current canonical graph, then discard only this target's stale preparation.
            pendingHierarchy.RemoveAll(change => ReferenceEquals(change.Target, target));
            foreach (var node in pending.Keys.Where(node => ReferenceEquals(node.PropertyGraph, registrations[target].Graph)).ToArray()) pending.Remove(node);
            ClearConflict(target);
        }
        catch (Exception error) { RecordConflict(target, error); throw; }
    }
}
