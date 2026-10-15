using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Records document edits in its session's actual stack, or an isolated standalone stack.</summary>
internal sealed class EditorDocumentHistoryScope
{
    private readonly IEditorDocument document;
    private readonly DocumentTransactionHistory? localPresentation;
    private EditorAuthoringState? state;
    private Guid? namedTransaction;
    private readonly string defaultName;
    public EditorDocumentHistoryScope(IEditorDocument document, EditorSessionHistory? history, string name, int capacity = 256)
    {
        this.document = document; SessionHistory = history; defaultName = name;
        Stack = history?.Stack ?? TransactionStackFactory.Create(capacity);
        if (history == null) localPresentation = new(Stack);
    }
    public EditorSessionHistory? SessionHistory { get; }
    internal EditorAuthoringState? AuthoringState => state;
    public ITransactionStack Stack { get; }
    public EditorHistorySnapshot History => SessionHistory?.History ?? localPresentation!.Snapshot;
    public ITransaction CreateTransaction(string? name = null)
    {
        if (!document.CanSave) throw new InvalidOperationException("This document is read-only.");
        var transaction = SessionHistory?.CreateTransaction(name ?? defaultName) ?? Stack.CreateTransaction();
        if (SessionHistory == null && name != null) localPresentation!.Name(name);
        namedTransaction = transaction.Id; return transaction;
    }
    public void Name(string name)
    {
        if (SessionHistory == null) localPresentation!.Name(name);
        else if (namedTransaction is { } id) SessionHistory.SetName(id, name);
        namedTransaction = null;
    }
    public void Record(Operation operation, params EditorAuthoringState[] additionalParticipants)
    {
        if (!document.CanSave) throw new InvalidOperationException("This document is read-only.");
        if (SessionHistory == null)
        {
            if (additionalParticipants.Length != 0) throw new InvalidOperationException("Cross-document operations require a shared authored session.");
            Stack.PushOperation(operation);
        }
        else SessionHistory.PushDocumentOperation(document, operation, [state ?? throw new InvalidOperationException("Bind the authored state before editing this document."), .. additionalParticipants]);
    }
    public void Bind(EditorAuthoringState owner)
    {
        if (SessionHistory == null || !ReferenceEquals(SessionHistory.Registry, owner.Registry)
            || !ReferenceEquals(owner.Document, document) || state != null && !ReferenceEquals(state, owner))
            throw new InvalidOperationException("The authored document cannot be bound to this session state.");
        state = owner;
    }
    public void Saved(byte[]? contentHash = null)
    {
        if (SessionHistory == null) localPresentation!.Saved();
        else if (contentHash != null && state != null) SessionHistory.Registry.NotifyOwnedFileSaved(state, contentHash);
    }
    internal void EnsureMaySave()
    {
        if (state != null && (!state.IsPublished || !ReferenceEquals(state.Registry.Find(document.Path), state)))
            throw new InvalidOperationException("Only the canonical published document can save this file.");
    }
    public void BeforeReplay() => namedTransaction = null;
    public void EnsureMayDispose()
    {
        if (state != null && !state.IsDisposed) throw new InvalidOperationException("Dispose session-authored documents through their ownership registry.");
    }
}
