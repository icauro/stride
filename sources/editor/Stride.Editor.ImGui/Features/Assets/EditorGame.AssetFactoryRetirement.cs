namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class FactorySourceRetirement(Task<FactorySourceBaseline>? task, CancellationTokenSource? cancellation,
        FactorySourceRequest? request, FactorySourceBaseline? baseline, ResourceSourceCopyOperation? copies)
    {
        internal Task<FactorySourceBaseline>? Task { get; } = task;
        internal CancellationTokenSource? Cancellation { get; } = cancellation;
        internal FactorySourceRequest? Request { get; } = request;
        internal FactorySourceBaseline? Baseline = baseline;
        internal readonly List<IDisposable> Owners = request?.Leases.ToList() ?? [];
        internal readonly ResourceSourceCopyCancellation? Copies = copies == null ? null : new(copies);
        internal bool Observed, CopiesRetired, CancellationDisposed;
    }
    private readonly List<FactorySourceRetirement> retiredFactorySources = [];
    private bool pumpingFactoryRetirements;

    private void RetireFactorySourceRequest(Task<FactorySourceBaseline>? task, CancellationTokenSource? cancellation,
        FactorySourceRequest? request, FactorySourceBaseline? baseline = null, bool cancel = false)
    {
        var copies = pendingResourceCopies; pendingResourceCopies = null; pendingResourcePackage = null;
        var retired = new FactorySourceRetirement(task, cancellation, request, baseline, copies);
        retiredFactorySources.Add(retired);
        Interlocked.Increment(ref detachedAssetImports);
        if (cancel)
        {
            try { cancellation?.Cancel(); }
            catch (Exception error) { outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Asset import", error.Message, error.ToString()); }
        }
        PumpFactorySourceRetirements();
    }
    private void PumpFactorySourceRetirements()
    {
        if (pumpingFactoryRetirements) return;
        pumpingFactoryRetirements = true;
        try
        {
        foreach (var retired in retiredFactorySources.ToArray())
        {
            if (retired.Task is { IsCompleted: false }) continue;
            if (!retired.Observed)
            {
                if (retired.Task?.IsCompletedSuccessfully == true) retired.Baseline ??= retired.Task.Result;
                if (retired.Task?.Exception is { } failure)
                {
                    // Observe the real task and retain any real read handles whose worker compensation failed.
                    var pending = new Stack<Exception>(); pending.Push(failure);
                    while (pending.Count != 0)
                    {
                        var error = pending.Pop();
                        if (error is FactorySourcePreparationFailure preparation && !retired.Owners.Contains(preparation.Sources))
                            retired.Owners.Add(preparation.Sources);
                        if (error is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
                    }
                }
                retired.Observed = true;
            }
            if (retired.Baseline != null)
            {
                RetireTemplateReferenceLease(retired.Baseline);
                if (retiredTemplateLeases.Contains(retired.Baseline)) continue;
                retired.Baseline = null;
            }
            // Preparation-failure handles are actual source readers too; retire them before owned copies are compensated.
            var readersPending = false;
            foreach (var reader in retired.Owners.OfType<SourceReadLeases>().ToArray())
            {
                RetireTemplateReferenceLease(reader);
                if (retiredTemplateLeases.Contains(reader)) readersPending = true; else retired.Owners.Remove(reader);
            }
            if (readersPending) continue;
            if (!retired.CopiesRetired && retired.Copies != null)
            {
                RetireTemplateReferenceLease(retired.Copies);
                if (retiredTemplateLeases.Contains(retired.Copies)) continue;
                retired.CopiesRetired = true;
            }
            if (!retired.CancellationDisposed && retired.Cancellation != null)
            {
                RetireTemplateReferenceLease(retired.Cancellation);
                if (retiredTemplateLeases.Contains(retired.Cancellation)) continue;
                retired.CancellationDisposed = true;
            }
            foreach (var lease in retired.Owners.ToArray())
            {
                RetireTemplateReferenceLease(lease);
                if (!retiredTemplateLeases.Contains(lease)) retired.Owners.Remove(lease);
            }
            if (retired.Owners.Count != 0) continue;
            if (retiredFactorySources.Remove(retired)) Interlocked.Decrement(ref detachedAssetImports);
        }
        }
        finally { pumpingFactoryRetirements = false; }
    }
}
