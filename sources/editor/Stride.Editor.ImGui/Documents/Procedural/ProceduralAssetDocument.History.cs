namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument : IEditorHistoryDocument, ISessionAuthoringDocument
{
    public EditorSessionHistory? SessionHistory => historyScope.SessionHistory;
    public void BindAuthoringState(EditorAuthoringState state) => historyScope.Bind(state);
    public EditorHistorySnapshot History => historyScope.History;
    private Stride.Core.Transactions.ITransaction CreateDocumentTransaction(string? name = null) => historyScope.CreateTransaction(name);
    private void RecordOperation(Stride.Core.Transactions.Operation operation) => historyScope.Record(operation);
}
