namespace Stride.Editor.ImGui;

internal sealed partial class AssetRelocationHistory
{
    private long historyDisplayVersion = -1;
    private EditorHistorySnapshot historyDisplay = new([], 0);
    public EditorHistorySnapshot History
    {
        get
        {
            if (sessionHistory != null) return sessionHistory.History;
            if (historyDisplayVersion == Version) return historyDisplay;
            var rows = history.Select((change, index) => new EditorHistoryEntry(change.Id,
                "Move asset: " + change.Before.UnqualifiedUrl + " → " + change.After.UnqualifiedUrl, index < position, false)).ToArray();
            historyDisplayVersion = Version; return historyDisplay = new(rows, position);
        }
    }
}
