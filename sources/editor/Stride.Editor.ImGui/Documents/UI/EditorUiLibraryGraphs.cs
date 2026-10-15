using Stride.UI.Assets;
using Stride.Assets.Quantum;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Canonical UI-library graph links retain their actual authored generations across replacement and undo.</summary>
internal sealed class EditorUiLibraryGraphs(EditorSessionHistory history)
{
    private sealed class Registration(UIAssetPropertyGraph graph)
    {
        internal UIAssetPropertyGraph Graph { get; } = graph;
        internal Dictionary<AssetId, (EditorAuthoringState State, IDisposable Lease)> Bases { get; } = [];
        internal List<IDisposable> RecoveryLeases { get; } = [];
    }
    private readonly Dictionary<UIDocument, Registration> registrations = [];
    private readonly HashSet<UIDocument> resolving = [];
    private readonly List<(UIDocument Target, AssetPropertyGraph Source, Action Reconcile)> pending = [];
    private bool detached;
    internal Func<AssetId, UIDocument?>? Resolve { get; set; }
    internal void Register(UIDocument document, UIAssetPropertyGraph graph)
    {
        if (registrations.ContainsKey(document)) return;
        registrations.Add(document, new(graph));
        graph.Container.PropagateHierarchyChangesFromBase = true;
        graph.Container.BaseHierarchyReconciliation = (source, reconcile) =>
        {
            if (!document.CanSave || !ReferenceEquals(history.Registry.Find(document.Asset.Id)?.Document, document)
                || history.Stack.RollInProgress || history.IsCompensating) return;
            if (!history.Stack.TransactionInProgress || !history.IsCapturingOperations) pending.Add((document, source, reconcile));
            else document.ApplyInheritedAssetMutation(reconcile, "Update UI library instance");
        };
        try { Refresh(document); }
        catch { Unregister(document); throw; }
    }
    internal void Refresh(UIDocument document)
    {
        if (detached || !registrations.TryGetValue(document, out var registration)) return;
        if (!resolving.Add(document)) throw new InvalidOperationException("UI library base graphs contain a cycle.");
        var acquired = new Dictionary<AssetId, (EditorAuthoringState State, IDisposable Lease)>();
        var graphs = new Dictionary<AssetId, UIAssetPropertyGraph>();
        var changed = false;
        try
        {
            var activeIds = document.Asset.Hierarchy.Parts.Values.Select(part => part.Base?.BasePartAsset.Id).OfType<AssetId>().ToHashSet();
            var ids = activeIds.Concat(registration.Graph.RetainedBaseAssetIds).Distinct().ToArray();
            foreach (var id in ids)
            {
                if (id == document.Asset.Id) throw new InvalidOperationException("A UI asset cannot inherit its own parts.");
                var state = history.Registry.Find(id);
                UIDocument? source;
                // Native graph undo retains real previous bases after their authored asset
                // is unpublished. Only inactive retained references may use that held lease.
                if (state == null && !activeIds.Contains(id) && registration.Bases.TryGetValue(id, out var retained)
                    && !retained.State.IsDisposed && ReferenceEquals(retained.State.Registry, history.Registry))
                { state = retained.State; source = state.Document as UIDocument; }
                else { source = Resolve?.Invoke(id); state = history.Registry.Find(id); }
                if (source == null) throw new InvalidOperationException("The source UI library is unavailable.");
                if (resolving.Contains(source)) throw new InvalidOperationException("UI library base graphs contain a cycle.");
                if (state == null || state.IsDisposed || state.AssetId != id || source.Asset.Id != id || !ReferenceEquals(state.Document, source)
                    || !ReferenceEquals(source.SessionHistory, history) || activeIds.Contains(id) && !state.IsPublished)
                    throw new InvalidOperationException("The UI library has no canonical authored owner.");
                var graph = source.UIHierarchyGraph;
                if (!ReferenceEquals(graph.Container.NodeContainer, history.HierarchyGraphs.Nodes)) throw new InvalidOperationException("UI library graphs must share session Quantum nodes.");
                acquired[id] = (state, state.AcquireHistory()); graphs[id] = graph;
            }
            changed = true;
            foreach (var pair in graphs)
            {
                registration.Graph.RebindRetainedBaseGraph(pair.Value, !history.Stack.RollInProgress && !history.IsCompensating);
                registration.Graph.Container.UnregisterGraph(pair.Key); registration.Graph.Container.RegisterGraph(pair.Value);
            }
            foreach (var id in registration.Bases.Keys.Except(acquired.Keys)) registration.Graph.Container.UnregisterGraph(id);
            registration.Graph.RefreshBase();
            foreach (var owner in registration.Bases.Values) owner.Lease.Dispose();
            registration.Bases.Clear(); foreach (var pair in acquired) registration.Bases.Add(pair.Key, pair.Value); acquired.Clear();
        }
        catch (Exception failure)
        {
            try
            {
                if (changed)
                {
                    foreach (var id in acquired.Keys.Concat(registration.Bases.Keys).Distinct()) registration.Graph.Container.UnregisterGraph(id);
                    foreach (var pair in registration.Bases)
                    {
                        var graph = ((UIDocument)pair.Value.State.Document).UIHierarchyGraph;
                        registration.Graph.RebindRetainedBaseGraph(graph, false); registration.Graph.Container.RegisterGraph(graph);
                    }
                    registration.Graph.RefreshBase();
                }
            }
            catch (Exception recovery)
            {
                registration.RecoveryLeases.AddRange(acquired.Values.Select(owner => owner.Lease)); acquired.Clear();
                throw new AggregateException("UI library graph links could not be restored.", failure, recovery);
            }
            throw;
        }
        finally { foreach (var owner in acquired.Values) owner.Lease.Dispose(); resolving.Remove(document); }
    }
    internal void RefreshPublished()
    {
        foreach (var document in registrations.Keys.Where(document => ReferenceEquals(history.Registry.Find(document.Asset.Id)?.Document, document)).ToArray()) Refresh(document);
    }
    internal void RegisterPreparedBase(UIDocument document, UIDocument source)
    {
        var graph = document.UIHierarchyGraph;
        var sourceGraph = source.UIHierarchyGraph;
        var state = history.Registry.Find(source.Asset.Id);
        if (state == null || !ReferenceEquals(state.Document, source) || !ReferenceEquals(source.SessionHistory, history))
            throw new InvalidOperationException("Publish the canonical UI library before preparing base links.");
        if (ReferenceEquals(document, source)) throw new InvalidOperationException("A UI asset cannot inherit its own parts.");
        var registration = registrations[document];
        if (registration.Bases.TryGetValue(source.Asset.Id, out var previous))
        {
            if (ReferenceEquals(previous.State, state)) return;
            throw new InvalidOperationException("The prepared UI library generation changed.");
        }
        var lease = state.AcquireHistory();
        try { graph.Container.RegisterGraph(sourceGraph); registration.Bases.Add(source.Asset.Id, (state, lease)); }
        catch { lease.Dispose(); throw; }
    }
    internal void ReconcilePublished(AssetId? changed)
    {
        if (changed == null || history.Stack.RollInProgress || history.IsCompensating) return;
        foreach (var pair in registrations.Where(pair => pair.Key.CanSave && pair.Value.Bases.ContainsKey(changed.Value)
            && ReferenceEquals(history.Registry.Find(pair.Key.Asset.Id)?.Document, pair.Key)).ToArray())
            pair.Key.ApplyInheritedAssetMutation(pair.Value.Graph.ReconcileHierarchyWithBase, "Update UI library instance");
    }
    internal void Flush(AssetMetadataDocument source)
    {
        if (source is not UIDocument ui || !registrations.TryGetValue(ui, out var registration)) return;
        var queued = pending.Where(change => ReferenceEquals(change.Source, registration.Graph)).ToArray();
        foreach (var change in queued) pending.Remove(change);
        foreach (var change in queued)
            if (change.Target.CanSave && ReferenceEquals(history.Registry.Find(change.Target.Asset.Id)?.Document, change.Target))
                change.Target.ApplyInheritedAssetMutation(change.Reconcile, "Update UI library instance");
    }
    internal void Unregister(UIDocument document)
    {
        if (!registrations.TryGetValue(document, out var registration)) return;
        registration.Graph.Container.BaseHierarchyReconciliation = null;
        registration.Graph.Container.PropagateHierarchyChangesFromBase = false;
        foreach (var id in registration.Bases.Keys) registration.Graph.Container.UnregisterGraph(id);
        registration.Graph.RefreshBase();
        foreach (var owner in registration.Bases.Values) owner.Lease.Dispose();
        foreach (var lease in registration.RecoveryLeases) lease.Dispose();
        registrations.Remove(document);
        pending.RemoveAll(change => ReferenceEquals(change.Target, document) || ReferenceEquals(change.Source, registration.Graph));
    }
    internal void DetachAll()
    {
        List<Exception>? failures = null;
        foreach (var document in registrations.Keys.ToArray())
            try { Unregister(document); } catch (Exception error) { (failures ??= []).Add(error); }
        if (failures != null) throw new AggregateException("UI library graph cleanup is incomplete.", failures);
        detached = true; pending.Clear();
    }
}
