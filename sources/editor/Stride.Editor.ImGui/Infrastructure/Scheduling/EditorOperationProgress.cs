using System.Diagnostics;

namespace Stride.Editor.ImGui;

internal enum EditorOperationProgressState { Running, Completed, Failed, Cancelled }
internal sealed record EditorOperationProgressSnapshot(string OwnerId, Guid Generation, string Title, string Message,
    float? Fraction, long Started, EditorOperationProgressState State, string Log, Action? Cancel)
{
    internal bool Running => State == EditorOperationProgressState.Running;
    internal int LogEntryCount { get; init; }
}

/// <summary>Editor-actor progress for actual operations; no worker calls or authored mutations.</summary>
internal sealed class EditorOperationProgress
{
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly List<EditorOperationProgressSnapshot> operations = [];
    internal void Set(string ownerId, string title, string message, float? fraction, Action? cancel)
    {
        Check(); ArgumentException.ThrowIfNullOrWhiteSpace(ownerId); ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (fraction.HasValue && (!float.IsFinite(fraction.Value) || fraction.Value < 0 || fraction.Value > 1))
            throw new ArgumentOutOfRangeException(nameof(fraction));
        var index = operations.FindIndex(operation => operation.OwnerId == ownerId);
        if (index < 0) operations.Add(new(ownerId, Guid.NewGuid(), title, message, fraction, Stopwatch.GetTimestamp(), EditorOperationProgressState.Running, "", cancel));
        else
        {
            var current = operations[index];
            if (!current.Running) throw new InvalidOperationException("The completed progress generation cannot be updated.");
            operations[index] = current with { Title = title, Message = message, Fraction = fraction, Cancel = cancel };
        }
    }
    internal void Append(string ownerId, string text)
    {
        Check(); ArgumentNullException.ThrowIfNull(text);
        var index = operations.FindIndex(operation => operation.OwnerId == ownerId);
        if (index < 0) return;
        var current = operations[index];
        operations[index] = current with
        {
            Log = current.LogEntryCount == 0 ? text : current.Log + Environment.NewLine + text,
            LogEntryCount = checked(current.LogEntryCount + 1),
        };
    }
    internal void Complete(string ownerId, Exception? error, bool cancelled)
    {
        Check();
        var index = operations.FindIndex(operation => operation.OwnerId == ownerId);
        if (index < 0) return;
        var current = operations[index];
        if (!current.Running) return;
        if (error == null && !cancelled) { operations.RemoveAt(index); return; }
        if (error != null) { Append(ownerId, error.ToString()); current = operations[index]; }
        operations[index] = current with
        {
            State = error != null ? EditorOperationProgressState.Failed : EditorOperationProgressState.Cancelled,
            Fraction = 1, Cancel = null,
        };
    }
    internal void Clear(string ownerId)
    { Check(); operations.RemoveAll(operation => operation.OwnerId == ownerId); }
    internal void Clear()
    { Check(); operations.Clear(); }
    internal EditorOperationProgressSnapshot? Next()
    { Check(); return operations.FirstOrDefault(); }
    internal IReadOnlyList<EditorOperationProgressSnapshot> Snapshot()
    { Check(); return operations.ToArray(); }
    internal void RequestCancel(string ownerId, Guid generation)
    {
        Check();
        var current = operations.FirstOrDefault(operation => operation.OwnerId == ownerId && operation.Generation == generation);
        if (current?.Running != true || current.Cancel == null) return;
        operations[operations.IndexOf(current)] = current with { Cancel = null };
        current.Cancel();
    }
    private void Check()
    {
        if (Environment.CurrentManagedThreadId != thread)
            throw new InvalidOperationException("Operation progress must be published on the editor actor.");
    }
}
