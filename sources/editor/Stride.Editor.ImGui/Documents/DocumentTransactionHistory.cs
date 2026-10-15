using System.Text.RegularExpressions;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Presentation of the document's actual core transaction stack, including its reachable save boundary.</summary>
internal sealed class DocumentTransactionHistory
{
    private readonly ITransactionStack stack;
    private readonly Dictionary<Guid, string> names = [];
    private Guid? pendingNameId, savePoint;
    private string? inProgressName;
    private bool dirty = true;
    private EditorHistorySnapshot snapshot = new([], 0);
    public DocumentTransactionHistory(ITransactionStack stack)
    {
        this.stack = stack;
        stack.TransactionCompleted += (_, args) =>
        {
            if (args.Transaction.Operations.Count == 0) return;
            pendingNameId = args.Transaction.Id;
            if (inProgressName != null) { names[args.Transaction.Id] = inProgressName; inProgressName = null; pendingNameId = null; }
            dirty = true;
        };
        stack.TransactionRollbacked += (_, _) => { pendingNameId = null; dirty = true; };
        stack.TransactionRollforwarded += (_, _) => { pendingNameId = null; dirty = true; };
        stack.TransactionDiscarded += (_, args) =>
        { foreach (var transaction in args.Transactions) names.Remove(transaction.Id); dirty = true; };
        stack.Cleared += (_, _) => { names.Clear(); pendingNameId = savePoint = null; dirty = true; };
    }
    public void Name(string name)
    {
        if (stack.TransactionInProgress) inProgressName = name;
        else if (pendingNameId is { } id) { names[id] = name; pendingNameId = null; }
        dirty = true;
    }
    public void Saved() { savePoint = stack.NextUndoTransaction?.Id; dirty = true; }
    public EditorHistorySnapshot Snapshot
    {
        get
        {
            if (!dirty) return snapshot;
            var transactions = stack.RetrieveAllTransactions().ToArray();
            var next = stack.NextRedoTransaction?.Id;
            var position = next == null ? transactions.Length : Array.FindIndex(transactions, transaction => transaction.Id == next);
            if (position < 0) position = transactions.Length;
            var rows = transactions.Select((transaction, index) => new EditorHistoryEntry(transaction.Id,
                names.GetValueOrDefault(transaction.Id) ?? OperationName(transaction), index < position, transaction.Id == savePoint)).ToArray();
            snapshot = new(rows, position); dirty = false; return snapshot;
        }
    }
    private static string OperationName(IReadOnlyTransaction transaction) => string.Join(", ", transaction.Operations.Select(operation =>
        Regex.Replace(operation.GetType().Name.Replace("Operation", ""), "(?<=[a-z])(?=[A-Z])", " ")).Distinct());
}
