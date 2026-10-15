using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Canonical Archetype links and actual Quantum nodes for retained metadata generations.</summary>
internal sealed class EditorAssetGraphs(EditorSessionHistory history)
{
    private sealed class Registration(AssetPropertyGraph graph)
    {
        internal AssetPropertyGraph Graph { get; } = graph;
        internal EditorAuthoringState? Base;
        internal IDisposable? BaseLease;
        internal List<IDisposable> RecoveryLeases { get; } = [];
    }
    private readonly Dictionary<IEditorAssetGraphDocument, Registration> registrations = [];
    private readonly HashSet<IEditorAssetGraphDocument> resolving = [];
    private readonly Dictionary<IAssetNode, IAssetNode> pending = [];
    private int initializing;
    internal Func<AssetId, IEditorAssetGraphDocument?>? Resolve { get; set; }
    internal bool IsRegistered(IEditorAssetGraphDocument document) => registrations.ContainsKey(document);
    internal void Register(IEditorAssetGraphDocument document, AssetPropertyGraph graph)
    {
        registrations.Add(document, new(graph));
        graph.Container.BaseChangeFilter = (change, target) =>
        {
            if (!document.CanSave || !ReferenceEquals(history.Registry.Find(document.AuthoredAsset.Id)?.Document, document)
                || history.Stack.RollInProgress || history.IsCompensating || target is not IAssetNode node) return false;
            if (history.Stack.TransactionInProgress && history.IsCapturingOperations) return true;
            if (change.Node is IAssetNode source) pending[node] = source;
            return false;
        };
        graph.Container.BaseReconciliation = (_, reconcile) =>
        {
            if (initializing != 0) reconcile();
            else document.ApplyInheritedAssetMutation(reconcile, "Update inherited asset");
        };
        try
        {
            Refresh(document); initializing++;
            try { graph.ReconcileWithBase(); }
            finally { initializing--; }
        }
        catch { Unregister(document); throw; }
    }
    internal void Refresh(IEditorAssetGraphDocument document)
    {
        if (!registrations.TryGetValue(document, out var registration)) return;
        history.Registry.RefreshDocumentFileProvenance(document, document.AuthoredAsset.Id);
        if (!resolving.Add(document)) throw new InvalidOperationException("Asset archetypes contain a cycle.");
        EditorAuthoringState? next = null; IDisposable? lease = null;
        try
        {
            if (history.HierarchyGraphs.ResolveAssetItem?.Invoke(document.AuthoredAsset.Id) is { } item)
                document.UpdateAuthoringAssetIdentity(item);
            if (document.AuthoredAsset.Archetype is { } archetype)
            {
                if (archetype.Id == document.AuthoredAsset.Id) throw new InvalidOperationException("An asset cannot inherit itself.");
                if (resolving.Any(active => active.AuthoredAsset.Id == archetype.Id)) throw new InvalidOperationException("Asset archetypes contain a cycle.");
                var source = Resolve?.Invoke(archetype.Id) ?? throw new InvalidOperationException("The asset archetype cannot be resolved.");
                next = history.Registry.Find(archetype.Id);
                if (next == null || !ReferenceEquals(next.Document, source)) throw new InvalidOperationException("The archetype has no canonical authored owner.");
                if (source.AuthoredAsset.GetType() != document.AuthoredAsset.GetType()) throw new InvalidOperationException("The archetype has an incompatible asset type.");
                var graph = source.AuthoringGraph;
                if (!ReferenceEquals(graph.Container.NodeContainer, history.HierarchyGraphs.Nodes)) throw new InvalidOperationException("Archetype graphs must share actual session nodes.");
                lease = next.AcquireHistory();
                registration.Graph.Container.UnregisterGraph(archetype.Id); registration.Graph.Container.RegisterGraph(graph);
            }
            if (registration.Base?.AssetId is { } previous && previous != next?.AssetId) registration.Graph.Container.UnregisterGraph(previous);
            registration.Graph.RefreshBase();
            registration.BaseLease?.Dispose(); registration.Base = next; registration.BaseLease = lease; lease = null;
        }
        catch (Exception failure)
        {
            try
            {
                if (next?.AssetId is { } current) registration.Graph.Container.UnregisterGraph(current);
                if (registration.Base is { } previous && previous.Document is IEditorAssetGraphDocument source)
                { registration.Graph.Container.UnregisterGraph(source.AuthoredAsset.Id); registration.Graph.Container.RegisterGraph(source.AuthoringGraph); }
                registration.Graph.RefreshBase();
            }
            catch (Exception recovery)
            {
                if (lease != null) { registration.RecoveryLeases.Add(lease); lease = null; }
                throw new AggregateException("Archetype graph links could not be restored.", failure, recovery);
            }
            throw;
        }
        finally { lease?.Dispose(); resolving.Remove(document); }
    }
    internal void Flush(IEditorAssetGraphDocument source)
    {
        if (!registrations.TryGetValue(source, out var registration)) return;
        if (history.HierarchyGraphs.ResolveAssetItem?.Invoke(source.AuthoredAsset.Id) is { } item) source.UpdateAuthoringAssetIdentity(item);
        foreach (var entry in pending.Where(entry => ReferenceEquals(entry.Value.PropertyGraph, registration.Graph)).ToArray())
        {
            pending.Remove(entry.Key);
            if (history.Registry.Find(entry.Key.PropertyGraph.Id)?.Document is not IEditorAssetGraphDocument target
                || !registrations.TryGetValue(target, out var current) || !ReferenceEquals(current.Graph, entry.Key.PropertyGraph)) continue;
            if (entry.Key.BaseNode != null) target.ApplyInheritedAssetMutation(() => entry.Key.PropertyGraph.ReconcileNodeWithBase(entry.Key), "Update inherited asset");
        }
    }
    internal void RefreshPublished()
    {
        foreach (var document in registrations.Keys.Where(document => ReferenceEquals(history.Registry.Find(document.AuthoredAsset.Id)?.Document, document)).ToArray()) Refresh(document);
    }
    internal void ReconcilePublished(AssetId? changedBase)
    {
        if (changedBase == null || history.Stack.RollInProgress || history.IsCompensating) return;
        foreach (var entry in registrations.Where(entry => entry.Key.CanSave && entry.Key.AuthoredAsset.Archetype?.Id == changedBase.Value
            && ReferenceEquals(history.Registry.Find(entry.Key.AuthoredAsset.Id)?.Document, entry.Key)).ToArray())
            entry.Key.ApplyInheritedAssetMutation(entry.Value.Graph.ReconcileWithBase, "Update inherited asset");
    }
    internal void Unregister(IEditorAssetGraphDocument document)
    {
        if (!registrations.TryGetValue(document, out var registration)) return;
        registration.Graph.Container.BaseChangeFilter = null; registration.Graph.Container.BaseReconciliation = null;
        registration.Graph.Container.PropagateChangesFromBase = false;
        registration.Graph.Archetype = null!; registration.Graph.RefreshBase(registration.Graph.RootNode, null);
        if (registration.Base?.AssetId is { } id) registration.Graph.Container.UnregisterGraph(id);
        registration.BaseLease?.Dispose(); foreach (var lease in registration.RecoveryLeases) lease.Dispose();
        registrations.Remove(document);
        foreach (var node in pending.Where(entry => ReferenceEquals(entry.Key.PropertyGraph, registration.Graph)
            || ReferenceEquals(entry.Value.PropertyGraph, registration.Graph)).Select(entry => entry.Key).ToArray()) pending.Remove(node);
    }
    internal void DetachAll()
    {
        List<Exception>? errors = null;
        foreach (var document in registrations.Keys.ToArray())
            try { Unregister(document); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors != null) throw new AggregateException("Archetype graph cleanup is incomplete.", errors);
    }
}
