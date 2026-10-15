namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class ResourceSourceRetirement(Task<PreparedResourceSourceCopy[]>? task, CancellationTokenSource? token,
        ResourceCopyRequest? request, ResourceCopyRequest? ready, ResourceSourceCopyOperation? operation, bool ownsResult, Task[] readers)
    {
        internal Task<PreparedResourceSourceCopy[]>? Task { get; } = task;
        internal CancellationTokenSource? Token { get; } = token;
        internal ResourceCopyRequest? Request { get; } = request;
        internal ResourceCopyRequest? Ready { get; } = ready;
        internal bool OwnsResult { get; } = ownsResult;
        internal Task[] Readers { get; } = readers;
        internal ResourceSourceCopyCancellation? Compensation { get; } = operation == null ? null : new(operation);
        internal readonly List<IDisposable> SourceReads = [];
        internal readonly List<IDisposable> Owners = new[] { request?.Producer, ready?.Producer }.OfType<IDisposable>().Distinct<IDisposable>(ReferenceEqualityComparer.Instance).ToList();
        internal bool Observed, Compensated, TokenRetired;
    }
    private readonly List<ResourceSourceRetirement> retiredResourceSources = [];
    private bool pumpingResourceRetirement;
    private void RetireResourceSourceRequest(Task<PreparedResourceSourceCopy[]>? task, CancellationTokenSource? token,
        ResourceCopyRequest? request, ResourceCopyRequest? ready = null, ResourceSourceCopyOperation? operation = null,
        bool ownsResult = true, Task[]? readers = null, bool cancel = false)
    {
        if (task == null && token == null && request == null && ready == null && operation == null) { PumpResourceSourceRetirements(); return; }
        var retired = new ResourceSourceRetirement(task, token, request, ready, operation, ownsResult, readers ?? []);
        retiredResourceSources.Add(retired); Interlocked.Increment(ref detachedAssetImports);
        if (cancel)
            try { token?.Cancel(); }
            catch (Exception error) { outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Asset import", error.Message, error.ToString()); }
        PumpResourceSourceRetirements();
    }
    private void PumpResourceSourceRetirements()
    {
        if (pumpingResourceRetirement) return;
        pumpingResourceRetirement = true;
        try
        {
            foreach (var retired in retiredResourceSources.ToArray())
            {
                if (retired.Task is { IsCompleted: false } || retired.Readers.Any(reader => !reader.IsCompleted)) continue;
                foreach (var reader in retired.Readers) if (reader.IsFaulted) _ = reader.Exception;
                if (!retired.Observed)
                {
                    if (retired.OwnsResult && retired.Task?.IsCompletedSuccessfully == true)
                        retired.SourceReads.Add(new SourceReadLeases(retired.Task.Result.Select(file => file.SourceRead).Distinct().ToList()));
                    if (retired.Task?.Exception is { } failure)
                    {
                        var pending = new Stack<Exception>(); pending.Push(failure);
                        while (pending.Count != 0)
                        {
                            var error = pending.Pop();
                            if (error is ResourceSourcePreparationFailure preparation && !retired.SourceReads.Contains(preparation.Sources)) retired.SourceReads.Add(preparation.Sources);
                            if (error is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
                        }
                    }
                    retired.Observed = true;
                }
                foreach (var source in retired.SourceReads.ToArray())
                { RetireTemplateReferenceLease(source); if (!retiredTemplateLeases.Contains(source)) retired.SourceReads.Remove(source); }
                if (retired.SourceReads.Count != 0) continue;
                if (!retired.Compensated && retired.Compensation != null)
                {
                    RetireTemplateReferenceLease(retired.Compensation);
                    if (retiredTemplateLeases.Contains(retired.Compensation)) continue;
                    retired.Compensated = true;
                }
                if (!retired.TokenRetired && retired.Token != null)
                {
                    RetireTemplateReferenceLease(retired.Token);
                    if (retiredTemplateLeases.Contains(retired.Token)) continue;
                    retired.TokenRetired = true;
                }
                foreach (var lease in retired.Owners.ToArray())
                { RetireTemplateReferenceLease(lease); if (!retiredTemplateLeases.Contains(lease)) retired.Owners.Remove(lease); }
                if (retired.Owners.Count != 0) continue;
                if (retiredResourceSources.Remove(retired)) Interlocked.Decrement(ref detachedAssetImports);
            }
        }
        finally { pumpingResourceRetirement = false; }
    }
}
