namespace Stride.Editor.ImGui;

internal interface IEditorDocumentGroupHost
{
    IReadOnlyList<EditorDocumentEntry> OpenDocuments { get; }
    EditorDocumentEntry? RequestedSelection { get; }
    EditorDocumentEntry? ActiveDocument { get; }
    bool NativeTabText { get; }
    bool NativeSelectionsRestored => true;
    bool DiagnosticControlsEnabled => false;
    void RecordDiagnosticTab(EditorDocumentEntry entry) { }
    void RecordDiagnosticCommand(EditorDocumentEntry entry, EditorDocumentGroupCommand command) { }
    void RecordDiagnosticClose(EditorDocumentEntry entry, string action) { }
    bool CanCloseCohort { get; }
    // Must certify real independent view ownership, including all concurrently visible selected engine documents.
    bool CanPresent(EditorDocumentGroup group, EditorDocumentEntry entry);
    bool CanMove(EditorDocumentGroupMove move);
    void Activate(EditorDocumentEntry entry);
    void ActivationRejected(EditorDocumentEntry entry) { }
    void DrawContents(EditorDocumentGroup group, EditorDocumentEntry entry, bool focused);
    void SetVisible(EditorDocumentGroup group, EditorDocumentEntry? entry, bool visible);
    void RequestClose(EditorDocumentEntry entry);
    void RequestCloseCohort(EditorDocumentEntry? keep);
    void ApplyPresentationOrder(IReadOnlyList<EditorDocumentEntry> order);
    bool MenuItem(string caption, bool enabled = true);
    // Runs outside all ImGui scopes; validates current document/workspace/view certificates before mutation.
    void Execute(Action action);
}
