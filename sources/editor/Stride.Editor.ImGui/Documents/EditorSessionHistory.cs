using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>One actual core operation stack for an authored workspace.</summary>
internal sealed partial class EditorSessionHistory : IDisposable
{
    private readonly Dictionary<Guid, string> names = [];
    private readonly Dictionary<Guid, EditorAuthoringState[]> operationOwners = [];
    private readonly DocumentTransactionHistory presentation;
    private bool disposed;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private long namesVersion, capturedNamesVersion = -1;
    private EditorHistorySnapshot? capturedPresentation;
    private EditorHistorySnapshot cachedHistory = new([], 0);
    private EditorOperationCapture? capture;
    private readonly List<EditorOperationCapture> failedCaptures = [];
    private Guid? savedBoundary;
    private bool savedBoundaryLost;
    internal bool IsCompensating { get; private set; }
    public EditorSessionHistory(EditorAuthoringRegistry registry, int capacity = 256)
    {
        Registry = registry; Stack = TransactionStackFactory.Create(capacity); presentation = new(Stack); HierarchyGraphs = new(this); AssetGraphs = new(this); UiLibraryGraphs = new(this);
        Stack.TransactionDiscarded += (_, args) =>
        {
            foreach (var transaction in args.Transactions)
            {
                names.Remove(transaction.Id);
                if (transaction.Id == savedBoundary) savedBoundaryLost = true;
                if (savedBoundary == null && args.Reason == DiscardReason.StackFull) savedBoundaryLost = true;
                foreach (var operation in Flatten(transaction)) operationOwners.Remove(operation.Id);
            }
            namesVersion++;
        };
        Stack.Cleared += (_, _) => { names.Clear(); operationOwners.Clear(); namesVersion++; };
        Stack.TransactionCompleted += (_, _) =>
        {
            var retained = Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToHashSet();
            foreach (var id in names.Keys.Where(id => !retained.Contains(id)).ToArray()) names.Remove(id);
            var operations = Stack.RetrieveAllTransactions().SelectMany(Flatten).Select(operation => operation.Id).ToHashSet();
            foreach (var id in operationOwners.Keys.Where(id => !operations.Contains(id)).ToArray()) operationOwners.Remove(id);
            namesVersion++;
        };
    }
    public EditorAuthoringRegistry Registry { get; }
    public ITransactionStack Stack { get; }
    internal EditorHierarchyGraphs HierarchyGraphs { get; }
    internal EditorAssetGraphs AssetGraphs { get; }
    internal EditorUiLibraryGraphs UiLibraryGraphs { get; }
    internal EditorExtensionSession? Extensions { get; set; }
    internal Action? BeforeSelectionChange { get; set; }
    internal bool IsCapturingOperations => capture != null;
    internal void RetainFailedInitialOperation(Operation operation, params EditorAuthoringState[] participants)
    {
        Check(); ArgumentNullException.ThrowIfNull(operation);
        if (participants.Length == 0 || participants.Any(owner => !ReferenceEquals(owner.Registry, Registry)))
            throw new ArgumentException("Failed preparation requires its actual session owners.", nameof(participants));
        var prepared = new EditorOperationCapture();
        try { prepared.Add(operation, participants.Distinct().ToArray()); prepared.RetainForRecovery(); failedCaptures.Add(prepared); }
        catch { prepared.Dispose(); throw; }
    }
    internal void PushDocumentOperation(IEditorDocument document, Operation operation, params EditorAuthoringState[] participants)
    {
        if (document is AssetMetadataDocument metadata)
        {
            if (capture != null) { AssetGraphs.Flush(metadata); UiLibraryGraphs.Flush(metadata); PushOperation(operation, participants); return; }
            using var derived = CaptureOperationsAtomic(() => { AssetGraphs.Flush(metadata); UiLibraryGraphs.Flush(metadata); });
            if (derived.Entries.Count == 0) { PushOperation(operation, participants); return; }
            try { PushOperation(new CompositeOperation(derived.Entries.Select(entry => entry.Operation).Append(operation)), derived.Participants.Concat(participants).Distinct().ToArray()); }
            catch { RestoreFailedCapture(derived); throw; }
            return;
        }
        if (document is not EntityHierarchyDocument hierarchy) { PushOperation(operation, participants); return; }
        HierarchyGraphs.RefreshAssetIdentity(hierarchy);
        if (capture != null) { HierarchyGraphs.Flush(hierarchy); PushOperation(operation, participants); return; }
        using var propagated = CaptureOperationsAtomic(() => HierarchyGraphs.Flush(hierarchy));
        if (propagated.Entries.Count == 0) { PushOperation(operation, participants); return; }
        try
        {
            PushOperation(new CompositeOperation(propagated.Entries.Select(entry => entry.Operation).Append(operation)),
                propagated.Participants.Concat(participants).Distinct().ToArray());
        }
        catch { RestoreFailedCapture(propagated); throw; }
    }
    public bool CanUndo => Stack.CanRollback;
    public bool CanRedo => Stack.CanRollforward;
    public EditorHistorySnapshot History
    {
        get
        {
            Check();
            var snapshot = presentation.Snapshot;
            if (ReferenceEquals(snapshot, capturedPresentation) && namesVersion == capturedNamesVersion) return cachedHistory;
            capturedPresentation = snapshot; capturedNamesVersion = namesVersion;
            return cachedHistory = new(snapshot.Entries.Select(entry => entry with { Name = names.GetValueOrDefault(entry.Id) ?? entry.Name }).ToArray(), snapshot.Position);
        }
    }
    public ITransaction CreateTransaction(string name, TransactionFlags flags = TransactionFlags.None)
    {
        Check();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var transaction = Stack.CreateTransaction(flags); names[transaction.Id] = name; namesVersion++; return transaction;
    }
    public void PushOperation(Operation operation, params EditorAuthoringState[] participants)
    {
        Check();
        ArgumentNullException.ThrowIfNull(operation);
        if (!Stack.TransactionInProgress || Stack.RollInProgress) throw new InvalidOperationException("Record operations inside a session transaction.");
        if (participants.Length == 0) throw new ArgumentException("An authored operation requires its real participants.", nameof(participants));
        if (!operation.HasEffect) return;
        var owners = participants.Distinct().ToArray();
        if (owners.Any(owner => !ReferenceEquals(owner.Registry, Registry))) throw new InvalidOperationException("An operation cannot span different authored sessions.");
        if (capture != null) { capture.Add(operation, owners); return; }
        var leases = new List<IDisposable>();
        try
        {
            foreach (var participant in owners)
            {
                if (!ReferenceEquals(participant.Registry, Registry)) throw new InvalidOperationException("An operation cannot span different authored sessions.");
                leases.Add(participant.AcquireHistory());
            }
            var retained = new RetainedOperation(operation, leases);
            Stack.PushOperation(retained); operationOwners[retained.Id] = owners;
        }
        catch { foreach (var lease in leases) lease.Dispose(); throw; }
    }
    public EditorAuthoringState[] GetParticipants(IReadOnlyTransaction transaction)
    {
        Check();
        return Flatten(transaction).SelectMany(operation => operationOwners.GetValueOrDefault(operation.Id) ?? []).Distinct().ToArray();
    }
    private static IEnumerable<Operation> Flatten(IReadOnlyTransaction transaction)
    {
        foreach (var operation in transaction.Operations)
        {
            yield return operation;
            if (operation is IReadOnlyTransaction nested)
                foreach (var child in Flatten(nested)) yield return child;
        }
    }
    public EditorOperationCapture CaptureOperations(Action action)
    {
        Check(); ArgumentNullException.ThrowIfNull(action);
        if (capture != null || Stack.RollInProgress) throw new InvalidOperationException("Operation preparation is already in progress.");
        var prepared = new EditorOperationCapture(); capture = prepared;
        try { action(); return prepared; }
        catch { prepared.Dispose(); throw; }
        finally { capture = null; }
    }
    public EditorOperationCapture CaptureOperationsAtomic(Action action, Action<Action>? restorationBoundary = null)
    {
        Check(); ArgumentNullException.ThrowIfNull(action);
        if (capture != null || Stack.RollInProgress) throw new InvalidOperationException("Operation preparation is already in progress.");
        var prepared = new EditorOperationCapture(); prepared.SetRestorationBoundary(restorationBoundary); capture = prepared;
        try { action(); return prepared; }
        catch (Exception failure)
        {
            try { RestoreFailedCapture(prepared); }
            catch (Exception recovery)
            {
                throw new AggregateException("Authored changes failed and could not be fully restored.", failure, recovery);
            }
            throw;
        }
        finally { capture = null; }
    }
    internal void RestoreFailedCapture(EditorOperationCapture prepared)
    {
        Check();
        var previouslyCompensating = IsCompensating;
        try { IsCompensating = true; prepared.RestoreApplied(); prepared.Dispose(); }
        catch
        {
            prepared.RetainForRecovery();
            if (!failedCaptures.Contains(prepared)) failedCaptures.Add(prepared);
            throw;
        }
        finally { IsCompensating = previouslyCompensating; }
    }
    internal void CompensateInitialMutation(Action restore)
    {
        Check(); ArgumentNullException.ThrowIfNull(restore);
        var previous = IsCompensating;
        try { IsCompensating = true; restore(); }
        finally { IsCompensating = previous; }
    }
    public void SetName(Guid transactionId, string name)
    {
        Check();
        if (names.ContainsKey(transactionId)) { names[transactionId] = name; namesVersion++; }
    }
    public void Undo() { Check(); if (CanUndo) Stack.Rollback(); }
    public void Redo() { Check(); if (CanRedo) Stack.Rollforward(); }
    public void NotifySaveAll(bool packageMetadataSaved)
    {
        Check();
        if (!packageMetadataSaved || Registry.PublishedStates.Any(state => state.Document.IsDirty))
            throw new InvalidOperationException("A session save point requires all authored data to be saved.");
        presentation.Saved();
        savedBoundary = Stack.NextUndoTransaction?.Id; savedBoundaryLost = false;
    }
    public void RevertToSavePoint()
    {
        Check();
        if (savedBoundaryLost) throw new InvalidOperationException("The saved state is outside retained history. Save the workspace before closing it.");
        var history = History;
        var target = savedBoundary == null ? 0 : history.Entries.Select((entry, index) => (entry, index))
            .Where(item => item.entry.Id == savedBoundary).Select(item => item.index + 1).DefaultIfEmpty(-1).Single();
        if (target < 0) throw new InvalidOperationException("The saved state is no longer available.");
        while (History.Position > target) Undo();
        while (History.Position < target) Redo();
    }
    public void Clear()
    {
        Check();
        if (Stack.TransactionInProgress || capture != null) throw new InvalidOperationException("Complete authored edits before clearing session history.");
        List<Exception>? errors = null;
        foreach (var prepared in failedCaptures.ToArray())
        {
            try { RestoreFailedCapture(prepared); failedCaptures.Remove(prepared); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors != null) throw new AggregateException("Prepared authored changes still require recovery.", errors);
        Stack.Clear(); names.Clear();
    }
    public void Dispose()
    {
        if (disposed) return;
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Dispose history on its owning editor thread.");
        if (!Stack.IsEmpty) Clear();
        if (Stack.TransactionInProgress || Stack.DiscardCleanupErrors.Count != 0)
            throw new InvalidOperationException("Complete history cleanup before disposing it.");
        BeforeSelectionChange = null;
        disposed = true;
    }
    private void Check() { ObjectDisposedException.ThrowIf(disposed, this); Registry.EnsureOwnerThread(); }
}
