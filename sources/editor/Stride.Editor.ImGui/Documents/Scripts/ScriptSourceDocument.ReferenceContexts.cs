using Microsoft.CodeAnalysis;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    private readonly Dictionary<(Guid Generation, ProjectId Project), (ScriptNativeReferenceContext Context, IDisposable Lease)> referenceContexts = [];
    internal ScriptNativeReferenceContext[] CaptureReferenceContexts() => referenceContexts.Values.Select(value => value.Context).ToArray();
    internal void PruneRetiredReferenceContexts()
    {
        foreach (var value in referenceContexts.Values.Where(value => value.Context.MayPrune).ToArray())
            SetReferenceContext((value.Context.Generation, value.Context.ProjectId), null);
    }
    internal void ApplyReferenceContext(ScriptNativeReferenceContext context)
    {
        if (!CanEditText) throw new InvalidOperationException("The originating source is read-only.");
        var key = (context.Generation, context.ProjectId);
        var before = referenceContexts.GetValueOrDefault(key).Context;
        var operation = new ReferenceContextOperation(this, before, context);
        try
        {
            SetReferenceContext(key, context);
            using var transaction = historyScope.CreateTransaction(context.Documents.IsEmpty ? "Change compiler references" : "Change source document information");
            historyScope.Record(operation);
        }
        catch { SetReferenceContext(key, before); operation.ReleasePreparation(); throw; }
    }
    private void SetReferenceContext((Guid Generation, ProjectId Project) key, ScriptNativeReferenceContext? context)
    {
        var next = context?.Acquire();
        referenceContexts.Remove(key, out var previous);
        if (context != null) referenceContexts.Add(key, (context, next!));
        previous.Lease?.Dispose();
    }
    private void ReleaseReferenceContexts()
    {
        foreach (var value in referenceContexts.Values) value.Lease.Dispose();
        referenceContexts.Clear();
    }
    private void ReleaseRetiredReferenceContext(ScriptNativeReferenceContext? context)
    {
        if (context is { IsRetired: true } && referenceContexts.TryGetValue((context.Generation, context.ProjectId), out var current)
            && ReferenceEquals(current.Context, context)) SetReferenceContext((context.Generation, context.ProjectId), null);
    }
    private sealed class ReferenceContextOperation(ScriptSourceDocument owner, ScriptNativeReferenceContext? before, ScriptNativeReferenceContext after) : Operation
    {
        private IDisposable? beforeLease = before?.Acquire(), afterLease = after.Acquire();
        private readonly (Guid Generation, ProjectId Project) key = (after.Generation, after.ProjectId);
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private void Replay(bool forward)
        {
            if (!ReferenceEquals(owner.referenceContexts.GetValueOrDefault(key).Context, forward ? before : after))
                throw new InvalidOperationException("The source's native reference context changed.");
            owner.SetReferenceContext(key, forward ? after : before);
        }
        internal void ReleasePreparation() { Interlocked.Exchange(ref beforeLease, null)?.Dispose(); Interlocked.Exchange(ref afterLease, null)?.Dispose(); }
        protected override void FreezeContent()
        {
            ReleasePreparation(); owner.ReleaseRetiredReferenceContext(before); owner.ReleaseRetiredReferenceContext(after);
        }
    }
}
