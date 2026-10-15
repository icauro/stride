namespace Stride.Editor.ImGui;

internal interface IEditorRecoveryHost
{
    string ProjectPath { get; }
    bool HasPendingChanges { get; }
    IReadOnlyList<EditorRecoveryRecord> CaptureDocuments();
    EditorRecoveryWorkspaceState CaptureWorkspace();
    void RestoreWorkspace(EditorRecoveryWorkspaceState state);
    string? WorkspaceDependencyFailure(EditorRecoveryWorkspaceState state);
    void RecordControl(string id);
    string[] LoadedAssemblies();
    void Restore(EditorRecoveryRecord record);
    void ReportFailure(Exception error);
    void DrawSelectableText(string id, string value);
}
