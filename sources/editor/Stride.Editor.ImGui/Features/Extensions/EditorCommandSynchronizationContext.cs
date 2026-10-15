using System.Collections.Concurrent;

namespace Stride.Editor.ImGui;

/// <summary>Pumps awaited native command continuations on the actual editor owner thread.</summary>
internal sealed class EditorCommandSynchronizationContext : SynchronizationContext
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> pending = new();
    private int asynchronousOperations;
    internal bool HasPendingCallbacks => !pending.IsEmpty || Volatile.Read(ref asynchronousOperations) != 0;
    public override void OperationStarted() => Interlocked.Increment(ref asynchronousOperations);
    public override void OperationCompleted() => Interlocked.Decrement(ref asynchronousOperations);
    public override void Post(SendOrPostCallback callback, object? state) => pending.Enqueue((callback, state));
    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (Environment.CurrentManagedThreadId == ownerThread) { callback(state); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => { try { callback(state); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } }, null);
        completion.Task.GetAwaiter().GetResult();
    }
    internal void Run(Action action)
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Native command callbacks require the editor owner thread.");
        var previous = Current; SetSynchronizationContext(this);
        try { action(); } finally { SetSynchronizationContext(previous); }
    }
    internal void Pump(int maximumCallbacks = 256)
    {
        Run(() => { for (var count = 0; count < maximumCallbacks && pending.TryDequeue(out var next); count++) next.Callback(next.State); });
    }
}
