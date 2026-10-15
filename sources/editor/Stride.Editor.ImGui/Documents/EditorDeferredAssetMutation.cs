using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>A real Quantum journal retained while a native command awaits editor-thread callbacks.</summary>
internal sealed class EditorDeferredAssetMutation : IDisposable
{
    private readonly EditorSessionHistory history;
    private readonly EditorAuthoringState owner;
    private readonly AssetPropertyGraph graph;
    private readonly EditorQuantumMutation journal;
    private readonly Func<IGraphNode, NodeIndex, bool> canEdit;
    private readonly Action flush, release;
    private readonly IDisposable lease;
    private bool settled, disposed, ownerReleased, guardsAttached = true;
    internal bool HasChanges => journal.HasEffect;
    internal Guid OperationId => journal.Id;
    internal EditorDeferredAssetMutation(EditorSessionHistory history, EditorAuthoringState owner, AssetPropertyGraph graph,
        EditorQuantumMutation journal, Func<IGraphNode, NodeIndex, bool> canEdit, Action flush, Action release)
    {
        this.history = history; this.owner = owner; this.graph = graph; this.journal = journal;
        this.canEdit = canEdit; this.flush = flush; this.release = release;
        lease = owner.AcquireHistory();
        graph.Changing += MemberChanging; graph.ItemChanging += ItemChanging;
    }
    private void Check()
    {
        owner.Registry.EnsureOwnerThread();
        if (!owner.IsPublished || !ReferenceEquals(owner.Registry.Find(owner.CanonicalPath), owner))
            throw new InvalidOperationException("The command no longer owns the canonical document.");
    }
    private void MemberChanging(object? sender, MemberNodeChangeEventArgs args)
    {
        Check();
        if (!canEdit(args.Member, NodeIndex.Empty)) throw new InvalidOperationException("The native command cannot edit a read-only or sealed property.");
    }
    private void ItemChanging(object? sender, ItemChangeEventArgs args)
    {
        Check();
        if (!canEdit(args.Collection, args.Index)) throw new InvalidOperationException("The native command cannot edit this collection item.");
    }
    private void DetachGuards()
    {
        if (!guardsAttached) return;
        graph.Changing -= MemberChanging; graph.ItemChanging -= ItemChanging; guardsAttached = false;
    }
    internal void Commit(string title)
    {
        Check();
        if (settled) throw new InvalidOperationException("The command mutation is already settled.");
        DetachGuards();
        if (!journal.HasEffect) { journal.Complete(); settled = true; return; }
        if (history.IsCapturingOperations)
        {
            flush(); journal.Complete(); history.PushOperation(journal, owner); settled = true; return;
        }
        using var propagated = history.CaptureOperationsAtomic(flush);
        try
        {
            journal.Complete();
            using var transaction = history.CreateTransaction(title);
            history.PushOperation(new CompositeOperation(propagated.Entries.Select(entry => entry.Operation).Append(journal)),
                propagated.Participants.Append(owner).Distinct().ToArray());
            settled = true;
        }
        catch { if (!settled) history.RestoreFailedCapture(propagated); throw; }
    }
    internal void Rollback()
    {
        owner.Registry.EnsureOwnerThread();
        if (settled) return;
        DetachGuards(); history.CompensateInitialMutation(journal.RestoreInitial); settled = true;
    }
    public void Dispose()
    {
        if (disposed) return;
        owner.Registry.EnsureOwnerThread();
        if (!settled) Rollback();
        journal.Dispose();
        if (!ownerReleased) { release(); ownerReleased = true; }
        lease.Dispose(); disposed = true;
    }
}
