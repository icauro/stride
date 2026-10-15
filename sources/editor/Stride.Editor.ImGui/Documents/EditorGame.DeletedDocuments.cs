namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record DeletedDocument(EditorDocumentEntry? Entry, int Index, EditorAuthoringState? State);
    private readonly Dictionary<IAssetReferenceDocument, DeletedDocument> deletedDocuments = [];
    private EditorDocumentEntry? activateAfterAssetChange;
    private void SetDeletedAssetDocuments(IReadOnlyList<IAssetReferenceDocument> targets, bool deleting)
    {
        var previousActive = documents.Active;
        if (deleting)
        {
            if (targets.Any(target => deletedDocuments.ContainsKey(target)))
                throw new InvalidOperationException("The asset document is already deleted.");
            var snapshots = targets.Select(target =>
            {
                var entry = documents.Find(target);
                var state = entry?.State ?? authoring?.Find(target.Path);
                if (authoring != null && (state == null || !state.IsPublished || !ReferenceEquals(state.Document, target)))
                    throw new InvalidOperationException("The asset document has no current authored owner.");
                return (Target: target, Snapshot: new DeletedDocument(entry, entry == null ? -1 : documents.IndexOf(entry), state));
            }).ToArray();
            var applied = new List<(IAssetReferenceDocument Target, DeletedDocument Snapshot)>();
            try
            {
                foreach (var item in snapshots)
                {
                    DetachDeletedDocument(item.Snapshot);
                    deletedDocuments.Add(item.Target, item.Snapshot); applied.Add(item);
                }
                SynchronizeDeletedDocumentViews(targets, true);
            }
            catch (Exception failure)
            {
                var recovery = new List<Exception>();
                foreach (var (target, snapshot) in applied.OrderBy(item => item.Snapshot.Index))
                    try { RestoreDeletedDocument(snapshot); deletedDocuments.Remove(target); }
                    catch (Exception error) { recovery.Add(error); }
                try { RestoreDeletedDocumentActivation(previousActive); }
                catch (Exception error) { recovery.Add(error); }
                if (recovery.Count != 0) { recovery.Insert(0, failure); throw new AggregateException("Deleted asset views could not be restored.", recovery); }
                throw;
            }
        }
        else
        {
            var snapshots = targets.Select(target => (Target: target, Snapshot: deletedDocuments.GetValueOrDefault(target)
                ?? throw new InvalidOperationException("The deleted asset document is no longer retained."))).OrderBy(item => item.Snapshot.Index).ToArray();
            if (snapshots.Any(item => item.Snapshot.Entry != null && documents.Find(item.Snapshot.Entry.Path) != null))
                throw new InvalidOperationException("A document is already open at a restored asset path.");
            var applied = new List<DeletedDocument>();
            try
            {
                foreach (var (_, snapshot) in snapshots) { RestoreDeletedDocument(snapshot); applied.Add(snapshot); }
                SynchronizeDeletedDocumentViews(targets, false);
            }
            catch (Exception failure)
            {
                var recovery = new List<Exception>();
                foreach (var snapshot in applied.AsEnumerable().Reverse())
                    try { DetachDeletedDocument(snapshot); } catch (Exception error) { recovery.Add(error); }
                try { SynchronizeDeletedDocumentViews(targets, true); RestoreDeletedDocumentActivation(previousActive); }
                catch (Exception error) { recovery.Add(error); }
                if (recovery.Count != 0) { recovery.Insert(0, failure); throw new AggregateException("Restored asset views could not be detached.", recovery); }
                throw;
            }
            foreach (var (target, _) in snapshots) deletedDocuments.Remove(target);
        }
    }
    private void DetachDeletedDocument(DeletedDocument snapshot)
    {
        if (snapshot.State != null) authoring!.Unpublish(snapshot.State);
        try { if (snapshot.Entry != null) documents.Detach(snapshot.Entry); }
        catch { if (snapshot.State != null) authoring!.Publish(snapshot.State); throw; }
    }
    private void RestoreDeletedDocument(DeletedDocument snapshot)
    {
        if (snapshot.State != null) authoring!.Publish(snapshot.State);
        try { if (snapshot.Entry != null) documents.Restore(snapshot.Entry, snapshot.Index); }
        catch { if (snapshot.State != null) authoring!.Unpublish(snapshot.State); throw; }
    }
    private void RestoreDeletedDocumentActivation(EditorDocumentEntry? previous)
    {
        if (previous != null && ReferenceEquals(documents.Find(previous.Document), previous))
        { documents.Activate(previous); activateAfterAssetChange = previous; }
    }
    private void SynchronizeDeletedDocumentViews(IReadOnlyList<IAssetReferenceDocument> targets, bool deleting)
    {
        if (deleting)
        {
            if (targets.Any(target => ReferenceEquals(document, target)))
            { SaveSceneCameraSettings(); engineViewport?.ClearScene(); document = null; inspectedEntity = null; componentErrors.Clear(); activeColorPopup = null; }
            if (targets.Any(target => ReferenceEquals(assetDocument, target)))
            { assetDocument = null; assetEditorFocused = false; assetEditError = null; }
            foreach (var target in targets)
            {
                ReleaseTextureSettingsDocument(target);
                if (SamePath(assetPreviewPath, target.Path))
                {
                    if (texturePreview != null) RetireEditorPreview(texturePreview);
                    if (modelPreview != null) RetireEditorPreview(modelPreview);
                    if (materialPreview != null) RetireEditorPreview(materialPreview);
                    texturePreview = null; modelPreview = null; materialPreview = null; assetPreviewPath = null;
                }
                if (SamePath(selectedAssetPath, target.Path)) selectedAssetPath = null;
                if (ReferenceEquals(closingDocument?.Document, target)) { closingDocument = null; openDocumentClose = false; }
                if (ReferenceEquals(pendingDocumentClose?.Document, target)) pendingDocumentClose = null;
                if (ReferenceEquals(selectDocumentTab?.Document, target)) selectDocumentTab = null;
            }
        }
        activateAfterAssetChange = documents.Active;
        assetCommandContext = documents.Active?.Document is ProceduralAssetDocument;
    }
    private void PumpAssetDocumentActivation()
    {
        if (activateAfterAssetChange is not { } entry) return;
        activateAfterAssetChange = null;
        if (ReferenceEquals(documents.Find(entry.Document), entry)) Execute(() => ActivateDocument(entry));
    }
    private void ForgetDeletedAssetDocuments(IReadOnlyList<IAssetReferenceDocument> targets)
    {
        foreach (var target in targets)
        {
            deletedDocuments.Remove(target);
            if (ReferenceEquals(activateAfterAssetChange?.Document, target)) activateAfterAssetChange = null;
            assetTagBatchHistory.RemoveDocument(target);
            assetRelocationHistory.RemoveDocument(target);
            ReleaseTextureSettingsDocument(target);
            assetTagDrafts.Remove(target.Path);
            InvalidateCompiledTexture(target.Path);
        }
    }
}
