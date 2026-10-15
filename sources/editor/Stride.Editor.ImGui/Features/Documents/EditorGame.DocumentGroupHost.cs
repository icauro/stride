namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorDocumentGroupPanel? documentGroupPanel;
    internal bool NativeDocumentRestorationSettled
        => !nativeDockSelectionRestore.HasPending && documentGroupPanel?.HasPendingNativeRestoration != true;
    private EditorDocumentGroupPanel DocumentGroupPanel => documentGroupPanel ??= new(new DocumentGroupHost(this));

    private sealed class DocumentGroupHost(EditorGame owner) : IEditorDocumentGroupHost
    {
        public IReadOnlyList<EditorDocumentEntry> OpenDocuments => owner.documents.Entries;
        public EditorDocumentEntry? RequestedSelection => owner.selectDocumentTab;
        public EditorDocumentEntry? ActiveDocument => owner.documents.Active;
        public bool NativeTabText => owner.nativeTabTextHooks != null;
        public bool NativeSelectionsRestored => !owner.nativeDockSelectionRestore.HasPending;
        public bool DiagnosticControlsEnabled => owner.Controls.Enabled;
        public void RecordDiagnosticCommand(EditorDocumentEntry entry, EditorDocumentGroupCommand command)
        {
            if (owner.Controls.Enabled) owner.Controls.Record("document-command/" + entry.Id + "/" + command);
        }
        public void RecordDiagnosticTab(EditorDocumentEntry entry) => owner.Controls.RecordDocumentTab("document-tab/" + entry.Id);
        public void RecordDiagnosticClose(EditorDocumentEntry entry, string action)
        {
            if (owner.Controls.Enabled) owner.Controls.Record("document-close/" + entry.Id + "/" + action);
        }
        public bool CanCloseCohort => owner.CanRequestDocumentCloseCohort;
        public bool CanPresent(EditorDocumentGroup group, EditorDocumentEntry entry)
            => entry.Document is EntityHierarchyDocument ? owner.CanPresentSceneDocument(entry) : owner.CanPresentNonSceneDocument(entry);
        public bool CanMove(EditorDocumentGroupMove move)
        {
            if (!ReferenceEquals(move.Entry.Document, move.Document) || !ReferenceEquals(move.Entry.State, move.State)
                || owner.IsAuthoringPipelineUpdating || owner.IsAssetPreviewUpdating || owner.projectLoading != null
                || owner.IsEditorGpuRetirementPending || owner.IsEditorGraphicsRecovering) return false;
            if (move.Entry.Document is EntityHierarchyDocument)
            {
                if (!owner.CanPresentSceneDocument(move.Entry)) return false;
            }
            else if (!owner.CanMoveNonSceneDocument(move)) return false;
            var source = owner.DocumentGroupPanel.Model.Find(move.Source);
            if (source == null) return false;
            var remaining = ReferenceEquals(source.Active, move.Entry)
                ? source.Items.Where(entry => !ReferenceEquals(entry, move.Entry))
                    .ElementAtOrDefault(Math.Min(source.Items.IndexOf(move.Entry), Math.Max(0, source.Items.Count - 2)))
                : source.Active;
            if (remaining != null && !CanPresent(source, remaining)) return false;
            foreach (var group in owner.DocumentGroupPanel.Model.Groups)
                if (group.Id != move.Source && group.Id != move.Destination && group.Active is { } active && !CanPresent(group, active)) return false;
            return true;
        }
        public void Activate(EditorDocumentEntry entry)
        {
            if (!owner.IsCurrentDocumentPresentation(entry)) throw new InvalidOperationException("The focused document owner is no longer current.");
            var document = entry.Document; var state = entry.State; var workspace = owner.workspace;
            owner.ValidatePendingPropertyStrings();
            if (!owner.IsCurrentDocumentPresentation(entry) || !ReferenceEquals(entry.Document, document)
                || !ReferenceEquals(entry.State, state) || !ReferenceEquals(owner.workspace, workspace))
                throw new InvalidOperationException("The focused document changed during pending validation.");
            owner.ActivateDocument(entry);
            owner.ClearAssetPropertyContext();
            if (ReferenceEquals(owner.selectDocumentTab, entry)) owner.selectDocumentTab = null;
            owner.focusDocumentPanel = false;
        }
        public void DrawContents(EditorDocumentGroup group, EditorDocumentEntry entry, bool focused)
        {
            using var captureOwner = owner.BeginDocumentCapture(entry, group);
            if (ReferenceEquals(owner.documents.Active, entry) && ReferenceEquals(owner.selectDocumentTab, entry))
            { owner.selectDocumentTab = null; owner.focusDocumentPanel = false; }
            if (entry.Document is EntityHierarchyDocument) owner.DrawSceneDocumentContents(group, entry, focused);
            else owner.DrawNonSceneDocumentContents(group, entry, focused);
        }
        public void ActivationRejected(EditorDocumentEntry entry)
        {
            if (!ReferenceEquals(owner.selectDocumentTab, entry)) return;
            owner.selectDocumentTab = null;
            owner.focusDocumentPanel = false;
        }
        public void SetVisible(EditorDocumentGroup group, EditorDocumentEntry? entry, bool visible)
        {
            owner.SetSceneDocumentVisibility(group, entry, visible);
            owner.SetNonSceneDocumentVisibility(group, entry, visible);
        }
        public void RequestClose(EditorDocumentEntry entry) => owner.RequestDocumentClose(entry);
        public void RequestCloseCohort(EditorDocumentEntry? keep) => owner.RequestDocumentCloseCohort(keep);
        public void ApplyPresentationOrder(IReadOnlyList<EditorDocumentEntry> order)
        {
            if (order.Count != owner.documents.Entries.Count) return;
            owner.documents.ApplyOrder(order.Select((entry, index) => (entry, index)).ToArray());
        }
        public bool MenuItem(string caption, bool enabled = true) => owner.DrawNativeMenuItem(caption, enabled: enabled);
        public void Execute(Action action) => owner.Execute(action);
    }

    private void DrawDocumentGroups()
    {
        assetEditorFocused = false;
        DocumentGroupPanel.Draw();
    }

    private void SetInitialDocumentGroupArea(uint center) => DocumentGroupPanel.SetDocumentArea(center, initializePrimary: true);
    private bool TryResetDocumentGroupLayout()
    {
        if (IsAuthoringPipelineUpdating || IsAssetPreviewUpdating || projectLoading != null
            || IsEditorGpuRetirementPending || IsEditorGraphicsRecovering) return false;
        var expectedWorkspace = workspace; var history = sessionHistory; var active = documents.Active;
        var entries = documents.Entries.ToArray();
        var owners = entries.Select(entry => (entry.Document, entry.State)).ToArray();
        return Services.Commands.TryExecute(() =>
        {
            ValidatePendingPropertyStrings();
            if (IsAuthoringPipelineUpdating || IsAssetPreviewUpdating || projectLoading != null
                || IsEditorGpuRetirementPending || IsEditorGraphicsRecovering)
                throw new InvalidOperationException("The document layout must wait for the current editor operation.");
            if (!ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(sessionHistory, history)
                || !ReferenceEquals(documents.Active, active) || !documents.Entries.SequenceEqual(entries)
                || entries.Where((entry, index) => !ReferenceEquals(entry.Document, owners[index].Document)
                    || !ReferenceEquals(entry.State, owners[index].State)).Any())
                throw new InvalidOperationException("The document layout owners changed during pending validation.");
            DocumentGroupPanel.ResetLayout();
            // Do not replay a prior split/floating topology over the new default.
            restoredDocumentGroupLayout = null;
            retainedDocumentGroupLayout = DocumentGroupWorkspaceIdentity is { } identity
                ? DocumentGroupPanel.CaptureLayout(identity) : null;
        });
    }
    private string? DocumentGroupWorkspaceIdentity => workspace == null ? null : workspace.OpenPath + "|" + workspace.PackagePath;
    private void CaptureCurrentDocumentGroupLayout()
    {
        if (documentGroupPanel != null && DocumentGroupWorkspaceIdentity is { } identity)
        {
            documentGroupPanel.Model.Synchronize(documents.Entries);
            CaptureDocumentGroupLayout(documentGroupPanel, identity);
        }
    }
    private void RestoreCurrentDocumentGroupLayout()
    {
        if (DocumentGroupWorkspaceIdentity is not { } identity || !documents.Entries.All(entry => entry.Document is not EntityHierarchyDocument || CanPresentSceneDocument(entry))) return;
        TryRestoreDocumentGroupLayout(DocumentGroupPanel, identity);
    }
    private void ClearDocumentGroupPresentation()
    {
        CaptureCurrentDocumentGroupLayout();
        ClearSceneDocumentViews();
        documentGroupPanel?.Clear();
    }
}
