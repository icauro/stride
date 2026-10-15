namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class DocumentCloseTarget(EditorDocumentEntry entry)
    {
        internal readonly EditorDocumentEntry Entry = entry;
        internal IEditorDocument Document = entry.Document;
        internal EditorAuthoringState? State = entry.State;
        internal bool Matches() => ReferenceEquals(Entry.Document, Document) && ReferenceEquals(Entry.State, State)
            && (State == null || State.IsPublished && !State.IsDisposed && ReferenceEquals(State.Registry.Find(Entry.Path), State));
        internal void AcceptDiscard() { Document = Entry.Document; State = Entry.State; }
    }
    private sealed class DocumentCloseCohort(object? workspace, object? history, DocumentCloseTarget[] targets, DocumentCloseTarget? keep)
    {
        internal readonly object? Workspace = workspace, History = history;
        internal readonly DocumentCloseTarget[] Targets = targets;
        internal readonly DocumentCloseTarget? Keep = keep;
        internal int Index;
    }
    private DocumentCloseCohort? documentCloseCohort;
    private bool pumpingDocumentCloseRequests;
    private bool CanPumpDocumentCloseRequests => !exitApproved && !closeRequested && !closePromptOpen
        && !IsEditorGraphicsUnavailable && !IsEditorGraphicsRecovering && !IsEditorGpuRetirementPending && !IsEditorGpuRetirementFinalizing
        && !IsAuthoringPipelineUpdating && projectLoading == null && assetPreviewBuild == null && !IsUserProjectBuilding;
    private bool CanRequestDocumentCloseCohort => CanPumpDocumentCloseRequests && documentCloseCohort == null
        && closingDocument == null && pendingDocumentClose == null;
    private void RequestDocumentCloseCohort(EditorDocumentEntry? keep)
    {
        if (!CanRequestDocumentCloseCohort || keep != null && !documents.Entries.Contains(keep)) return;
        var kept = keep == null ? null : new DocumentCloseTarget(keep);
        var targets = documents.Entries.Where(entry => !ReferenceEquals(entry, keep)).Select(entry => new DocumentCloseTarget(entry)).ToArray();
        if (targets.Length == 0) return;
        var cohort = new DocumentCloseCohort(workspace, sessionHistory, targets, kept);
        ValidatePendingPropertyStrings();
        if (!IsCurrentDocumentCloseCohort(cohort) || targets.Any(target => documents.Entries.Contains(target.Entry) && !target.Matches()))
            throw new InvalidOperationException("The document close cohort changed while finishing pending edits.");
        documentCloseCohort = cohort;
    }
    private bool IsCurrentDocumentCloseCohort(DocumentCloseCohort cohort)
        => ReferenceEquals(workspace, cohort.Workspace) && ReferenceEquals(sessionHistory, cohort.History)
            && (cohort.Keep == null || documents.Entries.Contains(cohort.Keep.Entry) && cohort.Keep.Matches());
    private void CancelDocumentCloseBatch(bool clearCurrent = false)
    {
        var cohort = documentCloseCohort; documentCloseCohort = null;
        if (!clearCurrent || cohort == null || cohort.Index >= cohort.Targets.Length) return;
        var target = cohort.Targets[cohort.Index];
        if (ReferenceEquals(closingDocument, target.Entry))
        { closingDocument = null; closingDocumentOwner = null; openDocumentClose = false; documentCloseError = null; }
        if (ReferenceEquals(pendingDocumentClose, target.Entry))
        { pendingDocumentClose = null; pendingDocumentCloseOwner = null; }
    }
    private void ClearDocumentCloseRequests()
    {
        CancelDocumentCloseBatch(clearCurrent: true);
        closingDocument = pendingDocumentClose = null;
        closingDocumentOwner = pendingDocumentCloseOwner = null;
        openDocumentClose = false; documentCloseError = null;
    }
    private bool ValidateDocumentCloseTarget(EditorDocumentEntry entry, IEditorDocument? owner)
    {
        if (owner == null || !documents.Entries.Contains(entry) || !ReferenceEquals(entry.Document, owner)) return false;
        if (documentCloseCohort is not { } cohort) return true;
        return IsCurrentDocumentCloseCohort(cohort) && cohort.Index < cohort.Targets.Length
            && ReferenceEquals(cohort.Targets[cohort.Index].Entry, entry) && cohort.Targets[cohort.Index].Matches();
    }
    private void AcceptDocumentCloseDiscard(EditorDocumentEntry entry)
    {
        closingDocumentOwner = entry.Document;
        if (documentCloseCohort is { } cohort && cohort.Index < cohort.Targets.Length
            && ReferenceEquals(cohort.Targets[cohort.Index].Entry, entry)) cohort.Targets[cohort.Index].AcceptDiscard();
    }
    private void PumpDocumentCloseRequests()
    {
        if (pumpingDocumentCloseRequests || !CanPumpDocumentCloseRequests) return;
        pumpingDocumentCloseRequests = true;
        try
        {
            if (documentCloseCohort is { } existing && !IsCurrentDocumentCloseCohort(existing))
            { CancelDocumentCloseBatch(clearCurrent: true); return; }
            if (pendingDocumentClose is { } absent && !documents.Entries.Contains(absent))
            { pendingDocumentClose = null; pendingDocumentCloseOwner = null; }
            if (pendingDocumentClose is { } pending)
            {
                var owner = pendingDocumentCloseOwner;
                if (!ValidateDocumentCloseTarget(pending, owner))
                {
                    pendingDocumentClose = null; pendingDocumentCloseOwner = null;
                    CancelDocumentCloseBatch(clearCurrent: true);
                    statusMessage = "The document close target is no longer current.";
                    return;
                }
                try
                {
                    CloseDocument(pending);
                    pendingDocumentClose = null; pendingDocumentCloseOwner = null;
                }
                catch (Exception error)
                {
                    pendingDocumentClose = null; pendingDocumentCloseOwner = null;
                    statusMessage = error.Message;
                    if (documents.Entries.Contains(pending) && ReferenceEquals(pending.Document, owner))
                    { closingDocument = pending; closingDocumentOwner = owner; documentCloseError = error.Message; openDocumentClose = true; }
                    return;
                }
            }
            if (closingDocument != null || documentCloseCohort is not { } cohort) return;
            while (cohort.Index < cohort.Targets.Length && !documents.Entries.Contains(cohort.Targets[cohort.Index].Entry)) cohort.Index++;
            if (cohort.Index == cohort.Targets.Length) { documentCloseCohort = null; return; }
            var target = cohort.Targets[cohort.Index];
            if (!target.Matches())
            { CancelDocumentCloseBatch(clearCurrent: true); statusMessage = "The document close target was replaced."; return; }
            try { RequestDocumentClose(target.Entry, fromBatch: true); }
            catch (Exception error)
            {
                statusMessage = error.Message;
                if (target.Matches() && documents.Entries.Contains(target.Entry))
                { closingDocument = target.Entry; closingDocumentOwner = target.Document; documentCloseError = error.Message; openDocumentClose = true; }
                else CancelDocumentCloseBatch(clearCurrent: true);
            }
        }
        finally { pumpingDocumentCloseRequests = false; }
    }
}
