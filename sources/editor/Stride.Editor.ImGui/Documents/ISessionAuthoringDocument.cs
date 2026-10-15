namespace Stride.Editor.ImGui;

internal interface ISessionAuthoringDocument : IEditorDocument
{
    EditorSessionHistory? SessionHistory { get; }
    void BindAuthoringState(EditorAuthoringState state);
}
