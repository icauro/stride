using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed record EditorRecordedOperation(Operation Operation, IReadOnlyList<EditorAuthoringState> Participants);

/// <summary>Temporary preparation payload of real operations; has no undo cursor or independent history.</summary>
internal sealed class EditorOperationCapture : IDisposable
{
    private readonly List<EditorRecordedOperation> entries = [];
    private readonly List<IDisposable> leases = [];
    private bool disposed;
    private bool restored;
    private bool published;
    internal bool IsPublished => published;
    private bool recoveryPending;
    private Action<Action>? restorationBoundary;
    private bool operationsRestored;
    internal void SetRestorationBoundary(Action<Action>? boundary) => restorationBoundary = boundary;
    internal void RetainForRecovery() => recoveryPending = true;
    public IReadOnlyList<EditorRecordedOperation> Entries { get { ObjectDisposedException.ThrowIf(disposed, this); return entries; } }
    public EditorAuthoringState[] Participants
    { get { ObjectDisposedException.ThrowIf(disposed, this); return entries.SelectMany(entry => entry.Participants).Distinct().ToArray(); } }
    internal void Add(Operation operation, EditorAuthoringState[] participants)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var acquired = new List<IDisposable>();
        try
        {
            foreach (var state in participants) acquired.Add(state.AcquireHistory());
            entries.Add(new(operation, participants)); leases.AddRange(acquired);
        }
        catch { foreach (var lease in acquired) lease.Dispose(); throw; }
    }
    public void RestoreApplied()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (published) throw new InvalidOperationException("Published operations must be undone through session history.");
        if (restored) return;
        void RestoreOperations()
        {
            if (operationsRestored) return;
            new CompositeOperation(entries.Select(entry => entry.Operation)).RestoreApplied(); operationsRestored = true;
        }
        if (restorationBoundary == null) RestoreOperations(); else restorationBoundary(RestoreOperations);
        restored = true;
        recoveryPending = false;
    }
    public void Publish(EditorSessionHistory history, string title, Func<Operation, Operation>? wrap = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (restored || published) throw new InvalidOperationException("These operations have already been restored or published.");
        if (entries.Count == 0) return;
        try
        {
            using var transaction = history.CreateTransaction(title);
            Operation operation = new CompositeOperation(entries.Select(entry => entry.Operation));
            if (wrap != null) operation = wrap(operation) ?? throw new InvalidOperationException("The publication wrapper returned no operation.");
            history.PushOperation(operation, Participants);
            published = true;
        }
        catch
        {
            if (!published) history.RestoreFailedCapture(this);
            throw;
        }
    }
    public void Dispose()
    {
        if (disposed || recoveryPending) return;
        foreach (var lease in leases) lease.Dispose();
        leases.Clear(); entries.Clear(); disposed = true;
    }
}
