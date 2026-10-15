namespace Stride.Editor.ImGui;

internal sealed partial class AssetTagBatchHistory
{
    private Guid? displayedLastBatch;
    private EditorHistorySnapshot historyDisplay = new([], 0);
    public EditorHistorySnapshot History
    {
        get
        {
            if (sessionHistory != null) return sessionHistory.History;
            var last = history.LastOrDefault()?[0].Edit.OperationId;
            if (last == displayedLastBatch && historyDisplay.Entries.Count == history.Count && historyDisplay.Position == position) return historyDisplay;
            var rows = history.Select((batch, index) => new EditorHistoryEntry(batch[0].Edit.OperationId!.Value,
                batch.Length == 1 ? "Edit asset tags" : "Edit asset tags: " + batch.Length + " assets", index < position, false)).ToArray();
            displayedLastBatch = last; return historyDisplay = new(rows, position);
        }
    }
}
