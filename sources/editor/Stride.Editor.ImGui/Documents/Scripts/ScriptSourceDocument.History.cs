namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument : IEditorHistoryDocument, ISessionAuthoringDocument
{
    public EditorSessionHistory? SessionHistory => historyScope.SessionHistory;
    public void BindAuthoringState(EditorAuthoringState state) => historyScope.Bind(state);
    public EditorHistorySnapshot History => historyScope.History;
}
