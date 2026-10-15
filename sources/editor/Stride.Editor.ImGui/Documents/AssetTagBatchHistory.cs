using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Groups document-owned tag transactions while guarding intervening authored edits.</summary>
internal sealed partial class AssetTagBatchHistory
{
    private readonly EditorSessionHistory? sessionHistory;
    public AssetTagBatchHistory(EditorSessionHistory? sessionHistory = null) => this.sessionHistory = sessionHistory;
    private sealed record Change(IAssetTagDocument Document, PreparedAssetTagsEdit Edit);
    private readonly List<Change[]> history = [];
    private readonly Dictionary<IAssetTagDocument, HashSet<Guid>> orphanedOperations = [];
    private int position;
    public bool CanUndo => sessionHistory?.CanUndo ?? (position > 0 && history[position - 1].All(change => change.Document.CanUndo
        && change.Document.NextUndoOperationId == change.Edit.OperationId && change.Document.EditRevision == change.Edit.AfterRevision && change.Document.AuthoredAsset.Tags.SequenceEqual(change.Edit.After)));
    public bool CanRedo => sessionHistory?.CanRedo ?? (position < history.Count && history[position].All(change => change.Document.CanRedo
        && change.Document.NextRedoOperationId == change.Edit.OperationId && change.Document.EditRevision == change.Edit.BeforeRevision && change.Document.AuthoredAsset.Tags.SequenceEqual(change.Edit.Before)));
    public GroupedHistoryOwnership GetOwnership(IEditorDocument? document, bool redo)
    {
        if (sessionHistory != null) return GroupedHistoryOwnership.None;
        if (document is not IAssetTagDocument tagged) return GroupedHistoryOwnership.None;
        var operation = redo ? tagged.NextRedoOperationId : tagged.NextUndoOperationId;
        if (operation == null) return GroupedHistoryOwnership.None;
        for (var index = 0; index < history.Count; index++)
            if (history[index].Any(change => ReferenceEquals(change.Document, tagged) && change.Edit.OperationId == operation))
                return (redo ? index == position && CanRedo : index == position - 1 && CanUndo)
                    ? GroupedHistoryOwnership.Ready : GroupedHistoryOwnership.Blocked;
        return orphanedOperations.TryGetValue(tagged, out var ids) && ids.Contains(operation.Value)
            ? GroupedHistoryOwnership.Blocked : GroupedHistoryOwnership.None;
    }
    public bool Apply(IEnumerable<(IAssetTagDocument Document, string[] Tags)> edits)
    {
        var prepared = new List<Change>();
        var seen = new HashSet<IAssetTagDocument>();
        var requested = edits.ToArray();
        foreach (var (document, tags) in requested)
        {
            if (sessionHistory != null && (document is not ISessionAuthoringDocument owner || !ReferenceEquals(owner.SessionHistory, sessionHistory)))
                throw new InvalidOperationException("Grouped tags require documents from the same authored session.");
            if (!seen.Add(document)) throw new InvalidOperationException("An asset appears twice in the tag operation.");
            document.CommitEdits();
        }
        foreach (var (document, tags) in requested)
        {
            if (document.PrepareTags(tags) is { } edit) prepared.Add(new Change(document, edit));
        }
        if (prepared.Count == 0) return false;
        if (sessionHistory != null)
        {
            try
            {
                using var capture = sessionHistory.CaptureOperationsAtomic(() =>
                {
                    foreach (var change in prepared) change.Edit.Apply();
                    foreach (var change in prepared) change.Edit.Commit();
                });
                capture.Publish(sessionHistory, prepared.Count == 1 ? "Edit asset tags" : "Edit asset tags: " + prepared.Count + " assets");
            }
            catch
            {
                foreach (var change in prepared.AsEnumerable().Reverse()) change.Edit.Restore();
                throw;
            }
            return true;
        }
        try
        {
            foreach (var change in prepared) change.Edit.Apply();
            foreach (var change in prepared) change.Edit.Commit();
        }
        catch
        {
            foreach (var change in prepared.AsEnumerable().Reverse()) change.Edit.Restore();
            throw;
        }
        if (position < history.Count)
        {
            foreach (var batch in history.Skip(position)) Remember(batch);
            history.RemoveRange(position, history.Count - position);
        }
        history.Add(prepared.ToArray()); position++;
        if (history.Count > 256) { Remember(history[0]); history.RemoveAt(0); position--; }
        return true;
    }
    public void Undo()
    {
        if (sessionHistory != null) { sessionHistory.Undo(); return; }
        if (position == 0) return;
        foreach (var change in history[position - 1]) change.Document.CommitEdits();
        if (!CanUndo) return;
        var completed = new List<Change>();
        try
        {
            foreach (var change in history[position - 1].Reverse())
            { change.Document.Undo(); completed.Add(change); }
        }
        catch
        {
            foreach (var change in completed.AsEnumerable().Reverse()) change.Document.Redo();
            throw;
        }
        position--;
    }
    public void Redo()
    {
        if (sessionHistory != null) { sessionHistory.Redo(); return; }
        if (position >= history.Count) return;
        foreach (var change in history[position]) change.Document.CommitEdits();
        if (!CanRedo) return;
        var completed = new List<Change>();
        try
        {
            foreach (var change in history[position])
            { change.Document.Redo(); completed.Add(change); }
        }
        catch
        {
            foreach (var change in completed.AsEnumerable().Reverse()) change.Document.Undo();
            throw;
        }
        position++;
    }
    public void RemoveDocument(IEditorDocument document)
    {
        if (sessionHistory != null) return;
        if (history.Any(batch => batch.Any(change => ReferenceEquals(change.Document, document))))
        {
            foreach (var batch in history) Remember(batch);
            history.Clear(); position = 0;
        }
        if (document is IAssetTagDocument tagged) orphanedOperations.Remove(tagged);
    }
    private void Remember(IEnumerable<Change> batch)
    {
        foreach (var change in batch)
            if (change.Edit.OperationId is { } id)
            {
                if (!orphanedOperations.TryGetValue(change.Document, out var ids)) orphanedOperations.Add(change.Document, ids = []);
                ids.Add(id);
            }
    }
    public void Clear() { history.Clear(); orphanedOperations.Clear(); position = 0; }
}
