using Stride.Editor.Thumbnails;

namespace Stride.Editor.ImGui;

// Visible-priority actor. A real factory owner must await DisposeAsync before
// disposing its registry: queued/in-flight immutable captures are its users.
internal sealed class NativeThumbnailResidentQueue : IAsyncDisposable
{
    private sealed class Request(NativeThumbnailCompilationSnapshot snapshot, int priority, long sequence,
        CancellationToken cancellation)
    {
        internal readonly NativeThumbnailCompilationSnapshot Snapshot = snapshot;
        internal readonly IDisposable ArtifactLease = snapshot.AcquireArtifactLease();
        internal readonly int Priority = priority;
        internal readonly long Sequence = sequence;
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        internal readonly TaskCompletionSource<NativeThumbnailBatchResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object state = new();
    private readonly Dictionary<string, Request> queued = new(StringComparer.Ordinal);
    private readonly List<Request> running = [];
    private readonly SemaphoreSlim available = new(0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Action<ProjectLoadState> progress;
    private readonly Task actor;
    private long sequence;
    private bool closing;
    private NativeThumbnailResidentWorker? worker;
    private readonly List<IAsyncDisposable> failedInitializations = [];
    private const int DefaultWorkerIdleMilliseconds = 30000;
    private readonly int workerIdleMilliseconds;
    private long workerLastActivity;
    private bool idleRetirementPending;
    private string? idleRetirementFailure;
    private int idleRetirementCount;
    private int workerStartCount, idleRetirementFailureCount;
    internal object InspectLifecycle()
    {
        lock (state) return new { queuedCount = queued.Count, runningCount = running.Count,
            ownedWorkerProcessId = worker?.ProcessId, ownedWorkerGeneration = worker?.Generation,
            idleRetirementPending, idleRetirementFailure, idleRetirementCount, workerStartCount, idleRetirementFailureCount,
            idleLimitMilliseconds = workerIdleMilliseconds, workerLastActivity,
            actorStatus = actor.Status.ToString(), closing,
            boundary = "Actual queue and owned child lifecycle; a retained process ID does not imply an exited cleanup owner is still running." };
    }
    internal NativeThumbnailResidentQueue(Action<ProjectLoadState> progress, TimeSpan? idleTimeout = null)
    {
        var milliseconds = idleTimeout?.TotalMilliseconds ?? DefaultWorkerIdleMilliseconds;
        if (!double.IsFinite(milliseconds) || milliseconds < 1 || milliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        workerIdleMilliseconds = checked((int)Math.Ceiling(milliseconds));
        this.progress = progress; actor = Pump();
    }

    internal Task<NativeThumbnailBatchResult> Submit(NativeThumbnailCompilationSnapshot snapshot, int priority,
        CancellationToken cancellation)
    {
        lock (state)
        {
            if (closing) throw new ObjectDisposedException(nameof(NativeThumbnailResidentQueue));
            if (actor.IsCompleted) throw new NativeThumbnailWorkerUnavailableException("The native thumbnail actor has stopped.", actor.Exception);
            var id = snapshot.Authored.Parameters.RootId;
            // Retain only the newest continuation for a changing asset. Cancellation
            // of an active predecessor is handled by the real worker, not disposal.
            foreach (var active in running.Where(request => request.Snapshot.Authored.Parameters.RootId == id)) active.Cancellation.Cancel();
            if (queued.Remove(id, out var previous)) CancelQueued(previous);
            var request = new Request(snapshot, priority, ++sequence, cancellation);
            queued[id] = request; available.Release(); return request.Completion.Task;
        }
    }

    private async Task Pump()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                if (idleRetirementPending)
                {
                    if (!await RetireIdleWorkerAsync().ConfigureAwait(false))
                        await Task.Delay(1000, shutdown.Token).ConfigureAwait(false);
                    continue;
                }
                var remaining = worker == null ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromMilliseconds(Math.Max(0, workerIdleMilliseconds - (Environment.TickCount64 - workerLastActivity)));
                if (!await available.WaitAsync(remaining, shutdown.Token).ConfigureAwait(false))
                {
                    lock (state)
                        if (worker != null && queued.Count == 0 && running.Count == 0
                            && Environment.TickCount64 - workerLastActivity >= workerIdleMilliseconds)
                            idleRetirementPending = true;
                    continue;
                }
                // Coalesce one visible request burst; this does not poll the UI or
                // delay completion of an already running production build.
                await Task.Delay(16, shutdown.Token).ConfigureAwait(false);
                NativeThumbnailBatchPlan? plan = null;
                lock (state)
                {
                    while (available.Wait(0)) { }
                    foreach (var cancelled in queued.Values.Where(request => request.Cancellation.IsCancellationRequested).ToArray())
                    { queued.Remove(cancelled.Snapshot.Authored.Parameters.RootId); CancelQueued(cancelled); }
                    foreach (var candidate in queued.Values.OrderBy(request => request.Priority).ThenBy(request => request.Sequence).ToArray())
                    {
                        try
                        {
                            var proposed = NativeThumbnailBatchPlan.Create(running.Select(request => request.Snapshot).Append(candidate.Snapshot).ToArray());
                            running.Add(candidate); queued.Remove(candidate.Snapshot.Authored.Parameters.RootId); plan = proposed;
                            if (running.Count == 16) break;
                        }
                        catch (InvalidDataException) when (running.Count != 0)
                        {
                            // A different generation/canonical revision remains
                            // queued; its real snapshot is never silently replaced.
                        }
                    }
                }
                if (plan == null) continue;
                try
                {
                    if (worker != null && worker.Generation != plan.Generation)
                    { await worker.DisposeAsync().ConfigureAwait(false); worker = null; }
                    if (worker == null)
                    { worker = await NativeThumbnailResidentWorker.StartAsync(plan, progress, shutdown.Token).ConfigureAwait(false); workerStartCount++; }
                    workerLastActivity = Environment.TickCount64;
                    var tokens = running.ToDictionary(request => request.Snapshot.Identity, request => request.Cancellation.Token, StringComparer.Ordinal);
                    var results = await worker.BuildAsync(plan, tokens, shutdown.Token).ConfigureAwait(false);
                    lock (state)
                    {
                        foreach (var request in running)
                        {
                            if (request.Cancellation.IsCancellationRequested) request.Completion.TrySetCanceled(request.Cancellation.Token);
                            else
                            {
                                var result = results.Single(item => item.Identity == request.Snapshot.Identity
                                    && item.AssetId == request.Snapshot.Authored.Parameters.RootId);
                                request.Completion.TrySetResult(result);
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    if (error is NativeThumbnailWorkerInitializationException initialization)
                        failedInitializations.Add(initialization.Cleanup);
                    lock (state)
                        foreach (var request in running)
                            if (request.Cancellation.IsCancellationRequested || shutdown.IsCancellationRequested) request.Completion.TrySetCanceled();
                            else request.Completion.TrySetException(error);
                    if (worker != null) { await worker.DisposeAsync().ConfigureAwait(false); worker = null; }
                }
                finally
                {
                    lock (state)
                    {
                        NativeThumbnailArtifactCleanup.DisposeAll(running.SelectMany(request => new IDisposable[] { request.Cancellation, request.ArtifactLease }));
                        running.Clear();
                        workerLastActivity = Environment.TickCount64;
                        if (queued.Count != 0) available.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally
        {
            try { if (worker != null) { await worker.DisposeAsync().ConfigureAwait(false); worker = null; } }
            finally
            {
                lock (state)
                {
                    CancelQueuedRequests(queued.Values.Concat(running)); queued.Clear(); running.Clear();
                }
            }
        }
    }

    private async Task<bool> RetireIdleWorkerAsync()
    {
        try
        {
            if (worker != null) await worker.DisposeAsync().ConfigureAwait(false);
            lock (state)
            {
                worker = null; idleRetirementPending = false; idleRetirementFailure = null; idleRetirementCount++;
            }
            return true;
        }
        catch (Exception error)
        {
            // Retain the actual failed worker owner. New submissions remain
            // queued with their job leases until this owner really releases.
            lock (state) { idleRetirementFailure = error.ToString(); idleRetirementFailureCount++; }
            return false;
        }
    }

    private Task? disposal;
    private bool shutdownReleased, availableReleased;
    public ValueTask DisposeAsync()
    {
        lock (state)
        {
            closing = true;
            if (disposal == null || disposal.IsFaulted || disposal.IsCanceled) disposal = Task.Run(DisposeCoreAsync);
            return new ValueTask(disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        if (!shutdownReleased) shutdown.Cancel();
        try { await actor.ConfigureAwait(false); }
        catch when (actor.IsCompleted)
        {
            // A faulted actor may have retained a worker whose actual cleanup
            // failed. Retry that owner rather than returning a false drain.
            _ = actor.Exception;
        }
        if (worker != null) { await worker.DisposeAsync().ConfigureAwait(false); worker = null; }
        while (failedInitializations.Count != 0)
        { await failedInitializations[^1].DisposeAsync().ConfigureAwait(false); failedInitializations.RemoveAt(failedInitializations.Count - 1); }
        lock (state)
        {
            CancelQueuedRequests(queued.Values.Concat(running)); queued.Clear(); running.Clear();
        }
        if (!shutdownReleased) { shutdown.Dispose(); shutdownReleased = true; }
        if (!availableReleased) { available.Dispose(); availableReleased = true; }
    }
    private static void CancelQueued(Request request)
    {
        var failures = new List<Exception>();
        if (!request.Completion.Task.IsCompleted)
        {
            var token = request.Cancellation.Token;
            try { request.Cancellation.Cancel(); } catch (Exception error) { failures.Add(error); }
            finally { request.Completion.TrySetCanceled(token); }
        }
        try { NativeThumbnailArtifactCleanup.DisposeAll([request.Cancellation, request.ArtifactLease]); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Native thumbnail cancelled request cleanup failed.", failures);
    }
    private static void CancelQueuedRequests(IEnumerable<Request> requests)
    {
        var failures = new List<Exception>();
        foreach (var request in requests)
            try { CancelQueued(request); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Native thumbnail request cleanup failed.", failures);
    }
}
