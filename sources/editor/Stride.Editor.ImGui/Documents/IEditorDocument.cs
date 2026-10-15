namespace Stride.Editor.ImGui;

internal interface IEditorDocument : IDisposable
{
    string Path { get; }
    bool IsDirty { get; }
    bool CanSave { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    void CommitEdits();
    void Save();
    void Undo();
    void Redo();
}
