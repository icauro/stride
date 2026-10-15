namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Func<bool> CaptureScriptPresentationAuthority(ScriptSourceDocument document)
    {
        var entry = documents.Find(document);
        var owner = authoring?.Find(document.Path);
        var expectedWorkspace = workspace;
        var expectedHistory = sessionHistory;
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        return () => CanStartAssetEdit
            && ReferenceEquals(workspace, expectedWorkspace) && ReferenceEquals(sessionHistory, expectedHistory)
            && entry != null && ReferenceEquals(documents.Find(document), entry)
            && ReferenceEquals(documents.Active, entry) && ReferenceEquals(entry.Document, document)
            && group != null && ReferenceEquals(DocumentGroupPanel.Model.Find(group.Id), group)
            && ReferenceEquals(DocumentGroupPanel.Model.Find(entry), group) && ReferenceEquals(group.Active, entry)
            && group.Items.Contains(entry)
            && ReferenceEquals(entry.State, owner) && owner is { IsPublished: true, IsDisposed: false }
            && ReferenceEquals(authoring?.Find(document.Path), owner) && ReferenceEquals(owner.Document, document);
    }
}
