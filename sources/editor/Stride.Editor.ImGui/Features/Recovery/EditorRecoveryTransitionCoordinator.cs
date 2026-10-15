namespace Stride.Editor.ImGui;

/// <summary>Async I/O only; every transition continuation and failure callback is pumped by the editor actor.</summary>
internal sealed class EditorRecoveryTransitionCoordinator
{
    private Task? pending;
    private Action? continuation;
    private Action<Exception>? failure;
    public bool Busy => pending != null;
    public EditorRecoveryPanel? ReplacementSource { get; private set; }
    public bool DiscardAuthorized { get; private set; }
    public void AuthorizeReplacement(EditorRecoveryPanel? source, bool discardAuthorized)
    {
        if (Busy) throw new InvalidOperationException("A recovery transition is already pending.");
        ReplacementSource = source; DiscardAuthorized = discardAuthorized;
    }
    public void AbortReplacement() { ReplacementSource = null; DiscardAuthorized = false; }
    public void Run(Func<Task> operation, Action next, Action<Exception> failed)
    {
        if (Busy) throw new InvalidOperationException("A recovery transition is already pending.");
        // The operation captures snapshots synchronously on this actor before its first await.
        try { pending = operation(); continuation = next; failure = failed; }
        catch (Exception error) { failed(error); }
    }
    public void Pump()
    {
        if (pending?.IsCompleted != true) return;
        var task = pending; var next = continuation; var failed = failure;
        pending = null; continuation = null; failure = null;
        try { task.GetAwaiter().GetResult(); next?.Invoke(); }
        catch (Exception error) { failed?.Invoke(error); }
    }
}
