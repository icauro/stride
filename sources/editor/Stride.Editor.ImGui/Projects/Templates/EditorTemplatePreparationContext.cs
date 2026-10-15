using System.Collections.Concurrent;

namespace Stride.Editor.ImGui;

/// <summary>Real async preparation continuations on the owning editor Update actor.</summary>
internal sealed class EditorTemplatePreparationContext : SynchronizationContext
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();
    private bool closed;
    private int operations;
    internal bool IsOwnerThread => Environment.CurrentManagedThreadId == ownerThread;
    internal bool HasPendingWork { get { lock (callbacks) return operations != 0 || !callbacks.IsEmpty; } }
    public override SynchronizationContext CreateCopy() => this;
    public override void OperationStarted()
    {
        lock (callbacks)
        {
            if (closed) throw new InvalidOperationException("The native preparation owner has closed.");
            operations++;
        }
    }
    public override void OperationCompleted()
    {
        lock (callbacks)
        {
            if (operations == 0) throw new InvalidOperationException("The native preparation operation was not started.");
            operations--;
        }
    }
    public override void Post(SendOrPostCallback callback, object? state)
    {
        lock (callbacks)
        {
            if (closed) throw new InvalidOperationException("The native preparation owner has closed.");
            callbacks.Enqueue((callback, state));
        }
    }
    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Native preparation cannot synchronously wait on the editor actor.");
        lock (callbacks)
            if (closed) throw new InvalidOperationException("The native preparation owner has closed.");
        using var scope = Enter(); callback(state);
    }
    internal Scope Enter() => new(this);
    internal void Pump()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("The native preparation actor changed.");
        for (var count = 0; count < 256 && callbacks.TryDequeue(out var callback); count++)
        { using var scope = Enter(); callback.Callback(callback.State); }
    }
    internal bool Close()
    {
        lock (callbacks)
        {
            if (operations != 0 || !callbacks.IsEmpty) return false;
            closed = true; return true;
        }
    }
    internal readonly struct Scope : IDisposable
    {
        private readonly SynchronizationContext? previous;
        internal Scope(EditorTemplatePreparationContext context)
        { previous = Current; SetSynchronizationContext(context); }
        public void Dispose() => SetSynchronizationContext(previous);
    }
}
