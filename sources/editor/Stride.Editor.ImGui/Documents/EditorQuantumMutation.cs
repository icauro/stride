using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>A temporary journal of actual Quantum events, published as one real operation.</summary>
internal sealed class EditorQuantumMutation : Operation, IDisposable
{
    private sealed record Change(IAssetNode Node, NodeIndex Index, ContentChangeType Type, object? Before, object? After, ItemId Item)
    {
        internal void Apply(bool forward)
        {
            if (Type == ContentChangeType.ValueChange) { ((IAssetMemberNode)Node).Update(forward ? After : Before); return; }
            var collection = (IAssetObjectNode)Node;
            if (Type == ContentChangeType.CollectionUpdate)
            { collection.Update(forward ? After : Before, Item == ItemId.Empty ? Index : collection.IdToIndex(Item)); return; }
            var adding = Type == ContentChangeType.CollectionAdd;
            var value = adding ? After : Before;
            if (forward == adding)
            {
                if (Item != ItemId.Empty && collection.HasId(Item)) return;
                if (Item == ItemId.Empty) collection.Add(value!, Index);
                else if (Index.IsEmpty) collection.Restore(value!, Item);
                else collection.Restore(value!, Index, Item);
            }
            else
            {
                if (Item != ItemId.Empty && !collection.HasId(Item)) return;
                collection.Remove(value!, Item == ItemId.Empty ? Index : collection.IdToIndex(Item));
            }
        }
    }
    private readonly AssetPropertyGraph graph;
    private readonly AssetGraphOverrideSnapshot before;
    private readonly List<Change> changes = [];
    private readonly Action restoreOwner, applyOwner;
    private readonly EditorQuantumPendingChanges unfinished;
    private AssetGraphOverrideSnapshot? after;
    private bool subscribed = true, effect;
    private int recoveryIndex = -1;
    internal EditorQuantumMutation(AssetPropertyGraph graph, Action restoreOwner, Action applyOwner)
    {
        this.graph = graph; this.restoreOwner = restoreOwner; this.applyOwner = applyOwner;
        before = new(graph, graph.RootNode);
        unfinished = new(graph);
        graph.Changed += OnMember; graph.ItemChanged += OnItem;
    }
    private void OnMember(object? _, AssetMemberNodeChangeEventArgs e)
    {
        changes.Add(new((IAssetNode)e.Member, NodeIndex.Empty, ContentChangeType.ValueChange, e.OldValue, e.NewValue, e.ItemId));
    }
    private void OnItem(object? _, AssetItemNodeChangeEventArgs e)
    {
        changes.Add(new((IAssetNode)e.Collection, e.Index, e.ChangeType, e.OldValue, e.NewValue, e.ItemId));
    }
    public override bool HasEffect => after == null ? changes.Count != 0 || !before.IsCurrent : effect;
    internal void Complete()
    {
        Dispose(); effect = changes.Count != 0 || !before.IsCurrent; after = new(graph, graph.RootNode);
        if (effect) applyOwner();
    }
    internal void RestoreInitial()
    {
        Dispose(); unfinished.Restore(); if (recoveryIndex < 0) recoveryIndex = changes.Count;
        while (recoveryIndex > 0) { changes[recoveryIndex - 1].Apply(false); recoveryIndex--; }
        before.Restore(); graph.RefreshBase(); restoreOwner();
    }
    protected override void Rollback() { if (after == null || recoveryIndex >= 0) RestoreInitial(); else Apply(false); }
    protected override void Rollforward() => Apply(true);
    private void Apply(bool forward)
    {
        var current = new AssetGraphOverrideSnapshot(graph, graph.RootNode);
        var completed = new List<Change>();
        using var incomplete = new EditorQuantumPendingChanges(graph);
        var propagate = graph.Container.PropagateChangesFromBase;
        graph.Container.PropagateChangesFromBase = false;
        try
        {
            foreach (var change in forward ? changes : changes.AsEnumerable().Reverse()) { completed.Add(change); change.Apply(forward); }
            (forward ? after! : before).Restore(); graph.RefreshBase(); (forward ? applyOwner : restoreOwner)();
        }
        catch (Exception failure)
        {
            try
            {
                incomplete.Restore();
                foreach (var change in completed.AsEnumerable().Reverse()) change.Apply(!forward);
                current.Restore(); graph.RefreshBase(); (forward ? restoreOwner : applyOwner)();
            }
            catch (Exception recovery) { throw new AggregateException("Asset inheritance replay and compensation failed.", failure, recovery); }
            throw;
        }
        finally { graph.Container.PropagateChangesFromBase = propagate; }
    }
    public void Dispose()
    {
        if (!subscribed) return;
        graph.Changed -= OnMember; graph.ItemChanged -= OnItem; subscribed = false;
        unfinished.Dispose();
    }
}
