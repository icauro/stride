namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument : IEditorHistoryDocument, ISessionAuthoringDocument
{
    public EditorSessionHistory? SessionHistory => historyScope.SessionHistory;
    public void BindAuthoringState(EditorAuthoringState state)
    {
        historyScope.Bind(state);
        InitializeGameSettings();
    }
    public EditorHistorySnapshot History => historyScope.History;
    private Stride.Core.Transactions.ITransaction CreateDocumentTransaction(string? name = null) => historyScope.CreateTransaction(name);
    private void RecordOperation(Stride.Core.Transactions.Operation operation) => historyScope.Record(PreserveAuthoringOverrides(operation));
}
