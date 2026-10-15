using Stride.Assets.Quantum;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Real Quantum graph identity, canonical base links and temporary propagation preparation.</summary>
internal sealed partial class EditorHierarchyGraphs(EditorSessionHistory history)
{
    private sealed class Registration(EntityHierarchyPropertyGraph graph, EventHandler<AssetMemberNodeChangeEventArgs> changed)
    {
        public EntityHierarchyPropertyGraph Graph { get; } = graph;
        public EventHandler<AssetMemberNodeChangeEventArgs> Changed { get; } = changed;
        public Dictionary<AssetId, (EditorAuthoringState State, IDisposable Lease)> Bases { get; } = [];
        public List<IDisposable> RecoveryLeases { get; } = [];
    }
    private readonly Dictionary<EntityHierarchyDocument, Registration> registrations = [];
    private readonly Dictionary<IAssetNode, IAssetNode> pending = [];
    private readonly List<(EntityHierarchyDocument Target, AssetPropertyGraph Source, Action Apply)> pendingHierarchy = [];
    private readonly HashSet<EntityHierarchyDocument> resolving = [];
    private bool detached;
    private int initializing;
    internal AssetNodeContainer Nodes { get; } = new() { NodeBuilder = { NodeFactory = new AssetNodeFactory() } };
    internal Func<AssetId, EntityHierarchyDocument?>? Resolve { get; set; }
    internal Func<AssetId, AssetItem?>? ResolveAssetItem { get; set; }
    internal void RefreshAssetIdentity(EntityHierarchyDocument document)
    {
        if (ResolveAssetItem?.Invoke(document.Asset.Id) is { } item) document.UpdateHierarchyAssetItemIdentity(item);
    }
    internal bool IsRegistered(EntityHierarchyDocument document) => registrations.ContainsKey(document);
    internal static bool IsScalar(Type type) => type.IsValueType || type == typeof(string)
        || type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory);
    internal void Register(EntityHierarchyDocument document, EntityHierarchyPropertyGraph graph)
    {
        history.Registry.EnsureOwnerThread();
        EventHandler<AssetMemberNodeChangeEventArgs> changed = (_, args) =>
        {
            if (initializing == 0 && graph.UpdatingPropertyFromBase && !history.Stack.RollInProgress && !history.IsCompensating && history.Stack.TransactionInProgress
                && args.Member is IAssetMemberNode member && IsScalar(member.Type)) document.RecordPropagatedScalar(member, args);
        };
        var registration = new Registration(graph, changed);
        registrations.Add(document, registration);
        graph.Container.PropagateHierarchyChangesFromBase = true;
        graph.Container.BaseChangeFilter = (change, target) =>
        {
            if (!document.CanSave || !ReferenceEquals(history.Registry.Find(document.Asset.Id)?.Document, document)) return false;
            if (target is not IAssetNode member) return false;
            if (history.Stack.RollInProgress || history.IsCompensating) return false;
            if (history.Stack.TransactionInProgress && history.IsCapturingOperations) return true;
            if (change.Node is IAssetNode source) pending[member] = source;
            return false;
        };
        graph.Container.BaseReconciliation = (node, reconcile) =>
        {
            if (initializing != 0 || history.Stack.RollInProgress || history.IsCompensating
                || node is IAssetMemberNode scalar && IsScalar(scalar.Type)) reconcile();
            else document.ApplyInheritedGraphMutation(reconcile, "Update prefab instance");
        };
        graph.Container.BaseHierarchyReconciliation = (source, reconcile) =>
        {
            if (!document.CanSave || !ReferenceEquals(history.Registry.Find(document.Asset.Id)?.Document, document)
                || history.Stack.RollInProgress || history.IsCompensating) return;
            if (!history.Stack.TransactionInProgress || !history.IsCapturingOperations) { pendingHierarchy.Add((document, source, reconcile)); return; }
            document.ApplyInheritedHierarchyReconciliation(reconcile, "Update prefab instance");
        };
        graph.Changed += changed;
        try
        {
            Refresh(document);
            initializing++;
            try { graph.ReconcileHierarchyWithBase(); }
            finally { initializing--; }
        }
        catch { Unregister(document); throw; }
    }
    internal void Refresh(EntityHierarchyDocument document)
    {
        if (detached || !registrations.TryGetValue(document, out var registration)) return;
        RefreshAssetIdentity(document);
        if (!resolving.Add(document)) throw new InvalidOperationException("Prefab base graphs contain a cycle.");
        var acquired = new Dictionary<AssetId, (EditorAuthoringState State, IDisposable Lease)>();
        var graphs = new Dictionary<AssetId, EntityHierarchyPropertyGraph>();
        var changed = false;
        try
        {
            var activeIds = document.Asset.Hierarchy.Parts.Values.Select(part => part.Base?.BasePartAsset.Id).OfType<AssetId>().ToHashSet();
            var ids = activeIds.Concat(registration.Graph.RetainedBaseAssetIds).Distinct().ToArray();
            foreach (var id in ids)
            {
                if (id == document.Asset.Id) throw new InvalidOperationException("A prefab cannot inherit its own parts.");
                var state = history.Registry.Find(id);
                EntityHierarchyDocument? source;
                // Removed native parts retain their exact real base graph for undo.
                // An unpublished source is valid only through this already-held
                // history lease, never as the base of a current authored part.
                if (state == null && !activeIds.Contains(id) && registration.Bases.TryGetValue(id, out var retained)
                    && !retained.State.IsDisposed && retained.State.HistoryReferences > 0
                    && ReferenceEquals(retained.State.Registry, history.Registry) && history.Registry.Owns(retained.State.Document))
                { state = retained.State; source = state.Document as EntityHierarchyDocument; }
                else { source = Resolve?.Invoke(id); state = history.Registry.Find(id); }
                if (source == null) throw new InvalidOperationException("The prefab base asset cannot be resolved: "
                    + System.Text.Json.JsonSerializer.Serialize(InspectBaseOwnership(document)));
                if (state == null || state.IsDisposed || state.AssetId != id || source.Asset.Id != id
                    || !ReferenceEquals(state.Document, source) || !ReferenceEquals(source.SessionHistory, history)
                    || activeIds.Contains(id) && !state.IsPublished)
                    throw new InvalidOperationException("The prefab base has no canonical authored owner: "
                        + System.Text.Json.JsonSerializer.Serialize(InspectBaseOwnership(document)));
                var sourceGraph = source.HierarchyGraph;
                if (!ReferenceEquals(sourceGraph.Container.NodeContainer, Nodes)) throw new InvalidOperationException("Prefab base graphs must share actual Quantum nodes.");
                acquired[id] = (state, state.AcquireHistory());
                graphs[id] = sourceGraph;
            }
            changed = true;
            foreach (var pair in graphs)
            {
                registration.Graph.RebindRetainedBaseGraph(pair.Value, !history.Stack.RollInProgress && !history.IsCompensating);
                registration.Graph.Container.UnregisterGraph(pair.Key); registration.Graph.Container.RegisterGraph(pair.Value);
            }
            foreach (var id in registration.Bases.Keys.Except(acquired.Keys).ToArray()) registration.Graph.Container.UnregisterGraph(id);
            registration.Graph.RefreshBase();
            foreach (var previous in registration.Bases.Values) previous.Lease.Dispose();
            registration.Bases.Clear();
            foreach (var pair in acquired) registration.Bases.Add(pair.Key, pair.Value);
            acquired.Clear();
        }
        catch (Exception failure)
        {
            RecordConflict(document, failure);
            try
            {
                if (changed)
                {
                    foreach (var id in acquired.Keys.Concat(registration.Bases.Keys).Distinct()) registration.Graph.Container.UnregisterGraph(id);
                    foreach (var pair in registration.Bases)
                    {
                        var previous = ((EntityHierarchyDocument)pair.Value.State.Document).HierarchyGraph;
                        registration.Graph.RebindRetainedBaseGraph(previous, false);
                        registration.Graph.Container.RegisterGraph(previous);
                    }
                    registration.Graph.RefreshBase();
                }
            }
            catch (Exception recovery)
            {
                registration.RecoveryLeases.AddRange(acquired.Values.Select(owner => owner.Lease));
                throw new AggregateException("Prefab graph links could not be restored.", failure, recovery);
            }
            foreach (var owner in acquired.Values) owner.Lease.Dispose();
            throw;
        }
        finally { resolving.Remove(document); }
    }
    internal object InspectBaseOwnership(EntityHierarchyDocument document)
    {
        if (!registrations.TryGetValue(document, out var registration)) return new { registered = false };
        var activeIds = document.Asset.Hierarchy.Parts.Values.Select(part => part.Base?.BasePartAsset.Id).OfType<AssetId>().ToHashSet();
        return new { registered = true, document = document.Asset.Id.ToString(),
            bases = activeIds.Concat(registration.Graph.RetainedBaseAssetIds).Distinct().Select(id =>
            {
                var published = history.Registry.Find(id);
                var held = registration.Bases.TryGetValue(id, out var previous) ? previous.State : null;
                return new { id = id.ToString(), active = activeIds.Contains(id), retained = registration.Graph.RetainedBaseAssetIds.Contains(id),
                    publishedGeneration = published?.Generation, heldGeneration = held?.Generation, heldPublished = held?.IsPublished,
                    heldDisposed = held?.IsDisposed, heldHistoryReferences = held?.HistoryReferences,
                    heldRegistryOwned = held != null && ReferenceEquals(held.Registry, history.Registry) && history.Registry.Owns(held.Document),
                    registeredNativeGraphId = registration.Graph.Container.TryGetGraph(id)?.Id.ToString() };
            }).ToArray() };
    }
    internal void RefreshPublished()
    {
        foreach (var document in registrations.Keys.Where(document => history.Registry.Find(document.Asset.Id)?.Document == document).ToArray()) Refresh(document);
    }
    internal void ReconcilePublishedBases(AssetId? changedBase)
    {
        if (changedBase == null) return;
        foreach (var pair in registrations.Where(pair => pair.Key.CanSave && pair.Value.Bases.ContainsKey(changedBase.Value)
            && ReferenceEquals(history.Registry.Find(pair.Key.Asset.Id)?.Document, pair.Key)).ToArray())
        {
            if (history.Stack.RollInProgress || history.IsCompensating) continue;
            else
            {
                try { pair.Key.ApplyInheritedHierarchyReconciliation(pair.Value.Graph.ReconcileHierarchyWithBase, "Update prefab instance"); ClearConflict(pair.Key); }
                catch (Exception failure) { RecordConflict(pair.Key, failure); throw; }
            }
        }
    }
    internal void RegisterPreparedBase(EntityHierarchyDocument document, EntityHierarchyDocument source)
    {
        RefreshAssetIdentity(source);
        var graph = document.HierarchyGraph;
        var sourceGraph = source.HierarchyGraph;
        var state = history.Registry.Find(source.Asset.Id);
        if (state == null || !ReferenceEquals(state.Document, source) || !ReferenceEquals(source.SessionHistory, history))
            throw new InvalidOperationException("Publish the canonical prefab before preparing its base links.");
        var registration = registrations[document];
        if (registration.Bases.TryGetValue(source.Asset.Id, out var previous))
        {
            if (ReferenceEquals(previous.State, state)) return;
            throw new InvalidOperationException("The prepared prefab base generation has changed.");
        }
        var lease = state.AcquireHistory();
        try { graph.Container.RegisterGraph(sourceGraph); registration.Bases.Add(source.Asset.Id, (state, lease)); }
        catch { lease.Dispose(); throw; }
    }
    internal void Flush(EntityHierarchyDocument source)
    {
        if (!registrations.TryGetValue(source, out var registration)) return;
        foreach (var change in pendingHierarchy.Where(change => ReferenceEquals(change.Source, registration.Graph)).ToArray())
        {
            if (history.Registry.Find(change.Target.Asset.Id)?.Document == change.Target)
            {
                try { change.Target.ApplyInheritedHierarchyReconciliation(change.Apply, "Update prefab instance"); ClearConflict(change.Target); }
                catch (Exception failure) { RecordConflict(change.Target, failure); throw; }
            }
            pendingHierarchy.Remove(change);
        }
        foreach (var pair in pending.Where(pair => ReferenceEquals(pair.Value.PropertyGraph, registration.Graph)).ToArray())
        {
            if (history.Registry.Find(pair.Key.PropertyGraph.Id)?.Document is not EntityHierarchyDocument target
                || !registrations.TryGetValue(target, out var active) || !ReferenceEquals(active.Graph, pair.Key.PropertyGraph)
                || pair.Key.BaseNode == null) { pending.Remove(pair.Key); continue; }
            try
            {
                if (pair.Key is IAssetMemberNode scalar && IsScalar(scalar.Type)) scalar.PropertyGraph.ReconcileScalarWithBase(scalar);
                else target.ApplyInheritedGraphMutation(() => pair.Key.PropertyGraph.ReconcileNodeWithBase(pair.Key), "Update prefab instance");
                ClearConflict(target); pending.Remove(pair.Key);
            }
            catch (Exception failure) { RecordConflict(target, failure); throw; }
        }
    }
    internal void Unregister(EntityHierarchyDocument document)
    {
        if (!registrations.TryGetValue(document, out var registration)) return;
        registration.Graph.Changed -= registration.Changed;
        registration.Graph.Container.BaseChangeFilter = null;
        registration.Graph.Container.BaseReconciliation = null;
        registration.Graph.Container.BaseHierarchyReconciliation = null;
        registration.Graph.Container.PropagateHierarchyChangesFromBase = false;
        registration.Graph.Container.PropagateChangesFromBase = false;
        foreach (var id in registration.Bases.Keys) registration.Graph.Container.UnregisterGraph(id);
        registration.Graph.RefreshBase();
        foreach (var owner in registration.Bases.Values) owner.Lease.Dispose();
        foreach (var lease in registration.RecoveryLeases) lease.Dispose();
        registrations.Remove(document);
        if (!ReferenceEquals(history.Registry.Find(document.Asset.Id)?.Document, document)) ClearConflict(document);
        pendingHierarchy.RemoveAll(change => ReferenceEquals(change.Target, document) || ReferenceEquals(change.Source, registration.Graph));
        foreach (var member in pending.Where(pair => ReferenceEquals(pair.Key.PropertyGraph, registration.Graph)
            || ReferenceEquals(pair.Value.PropertyGraph, registration.Graph)).Select(pair => pair.Key).ToArray()) pending.Remove(member);
    }
    internal void DetachAll()
    {
        List<Exception>? errors = null;
        foreach (var document in registrations.Keys.ToArray())
            try { Unregister(document); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors != null) throw new AggregateException("Prefab base graph cleanup is incomplete.", errors);
        pending.Clear(); pendingHierarchy.Clear(); conflicts.Clear(); detached = true;
    }
}
