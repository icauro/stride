namespace Stride.Editor.ImGui;

internal sealed partial class PackageFolderHistory
{
    private DocumentTransactionHistory? historyPresentation;
    private DocumentTransactionHistory HistoryPresentation => historyPresentation ??= new(transactions);
    public EditorHistorySnapshot History => sessionHistory?.History ?? HistoryPresentation.Snapshot;
    public void NotifySaved() { if (sessionHistory == null) HistoryPresentation.Saved(); }
}
