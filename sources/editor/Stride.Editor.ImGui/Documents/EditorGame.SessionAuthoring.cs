using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorAuthoringRegistry? authoring;
    private EditorSessionHistory? sessionHistory;
    private AssetId[] observedDeletedSelection = [];
    private string? authoringCleanupError;
    internal EditorSessionHistory? SessionAuthoringHistory => sessionHistory;
    private void InitializeSessionAuthoring(ProjectWorkspace current)
    {
        authoring = new(IsReadOnlyAssetPath); sessionHistory = new(authoring); documents.Configure(authoring);
        sessionHistory.BeforeSelectionChange = BeforeNativeSelectionChange;
        assetBrowser.BeforeSelectionChange = BeforeNativeSelectionChange;
        assetBrowser.AfterSelectionChange = NotifyNativeAssetSelection;
        foreach (var package in current.Session.LocalPackages.Where(package => !package.IsReadOnly && package.State >= PackageState.AssetsReady))
            authoring.GetOrCreate(package.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(current, package, sessionHistory));
        sessionHistory.HierarchyGraphs.ResolveAssetItem = current.Session.FindAsset;
        sessionHistory.HierarchyGraphs.ConflictReported = ReportPrefabConflict;
        sessionHistory.UiLibraryGraphs.Resolve = id =>
        {
            if (authoring.Find(id)?.Document is UIDocument retained) return retained;
            var item = current.Session.FindAsset(id);
            if (item?.Asset is not Stride.UI.Assets.UILibraryAsset) return null;
            var path = item.FullPath.ToOSPath();
            return GetCanonicalDocument(path, () => new UIDocument(path, sessionHistory, item.Package.IsReadOnly)) as UIDocument;
        };
        sessionHistory.AssetGraphs.Resolve = id =>
        {
            if (authoring.Find(id)?.Document is IEditorAssetGraphDocument retained) return retained;
            var item = current.Session.FindAsset(id);
            if (item == null || item.Asset is SourceCodeAsset) return null;
            var path = item.FullPath.ToOSPath();
            return GetCanonicalDocument(path, () => item.Asset is Stride.Assets.Scripts.VisualScriptAsset
                ? new VisualScriptDocument(path, sessionHistory, item.Package.IsReadOnly)
                : item.Asset is Stride.UI.Assets.UIAssetBase ? new UIDocument(path, sessionHistory, item.Package.IsReadOnly)
                : item.Asset is Stride.Assets.Models.ProceduralModelAsset ? new ProceduralAssetDocument(path, sessionHistory, item.Package.IsReadOnly)
                : new AssetMetadataDocument(path, sessionHistory, item.Package.IsReadOnly)) as IEditorAssetGraphDocument;
        };
        sessionHistory.HierarchyGraphs.Resolve = id =>
        {
            if (authoring.Find(id)?.Document is EntityHierarchyDocument retained) return retained;
            var item = current.Session.FindAsset(id);
            if (item?.Asset is not Stride.Assets.Entities.PrefabAsset) return null;
            var path = item.FullPath.ToOSPath();
            return GetCanonicalDocument(path, () => new PrefabDocument(path, sessionHistory, item.Package.IsReadOnly)) as PrefabDocument;
        };
        assetTagBatchHistory = new(sessionHistory); assetRelocationHistory = new(sessionHistory);
        assetDeletionHistory = new(sessionHistory); packageFolderHistory = new(sessionHistory, current);
        observedDeletedSelection = assetDeletionHistory.DesiredSelection;
        sessionHistory.Stack.TransactionRollbacked += OnSessionHistoryChanged;
        sessionHistory.Stack.TransactionRollforwarded += OnSessionHistoryChanged;
    }
    private IEditorDocument GetCanonicalDocument(string path, Func<IEditorDocument> create)
    {
        var existing = authoring?.Find(path);
        if (existing != null)
        {
            if (existing.Document is VisualScriptDocument retained) InitializeVisualScriptDocument(retained);
            return existing.Document;
        }
        var next = create();
        if (next is VisualScriptDocument visualScript) InitializeVisualScriptDocument(visualScript);
        if (authoring == null) return next;
        var id = workspace?.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), Path.GetFullPath(path)))?.Id;
        try { return authoring.Register(next, id).Document; }
        catch { if (!authoring.Owns(next)) next.Dispose(); throw; }
    }
    private void OnSessionHistoryChanged(object? sender, TransactionEventArgs args)
        => Execute(() => SynchronizeSessionHistory(args));
    private void SynchronizeSessionHistory(TransactionEventArgs args)
    {
        if (sessionHistory == null) return;
        var participants = sessionHistory.GetParticipants(args.Transaction);
        sessionHistory.HierarchyGraphs.RefreshPublished();
        sessionHistory.AssetGraphs.RefreshPublished();
        sessionHistory.UiLibraryGraphs.RefreshPublished();
        foreach (var state in participants)
        {
            if (state.Document is IAssetTagDocument tagged && state.IsPublished)
                assetBrowser.UpdateAuthoredTags(tagged.AuthoredAsset.Id, tagged.AuthoredAsset.Tags);
            if (state.Document is AssetMetadataDocument) InvalidateCompiledTexture(state.Document.Path);
        }
        workspace?.RefreshAssets();
        if (workspace != null) assetBrowser.RefreshEntries(workspace);
        if (!ReferenceEquals(observedDeletedSelection, assetDeletionHistory.DesiredSelection))
        { observedDeletedSelection = assetDeletionHistory.DesiredSelection; RefreshDeletedAssets(); }
        RefreshRelocatedAssets(); SyncAuthoredAssetTags();
        foreach (var producer in participants.Select(state => state.AssetId).Where(id => id != null).Distinct()
            .Select(id => authoring?.Find(id!.Value)).Where(state => state is { IsPublished: true }).Cast<EditorAuthoringState>())
        {
            Action? restore = null;
            var retainedGeneratedReplay = participants.Any(state => state.Document is ScriptSourceDocument source
                && source.GeneratedByAssetId == producer.AssetId && state.IsPublished
                && ReferenceEquals(authoring?.Find(state.CanonicalPath), state)
                && ReferenceEquals(documents.Find(source)?.State, state));
            try { RefreshVisualScriptDerivedPresentations(producer, out restore, refreshText: !retainedGeneratedReplay); }
            catch (Exception failure)
            {
                try { restore?.Invoke(); }
                catch (Exception recovery) { throw new AggregateException("Derived source history refresh failed and could not be restored.", failure, recovery); }
                throw;
            }
        }
    }
    private void PumpSessionAuthoringCleanup()
    {
        if (authoring == null || sessionHistory == null) return;
        try
        {
            authoring.CollectUnused();
            var cleanup = sessionHistory.Stack.DiscardCleanupErrors;
            if (cleanup.Count != 0) throw new AggregateException("History resource cleanup is incomplete.", cleanup);
            authoringCleanupError = null;
        }
        catch (Exception error)
        {
            if (authoringCleanupError == error.ToString()) return;
            authoringCleanupError = error.ToString();
            outputLog.Append("Project", LogMessageType.Error, "Authored cleanup", authoringCleanupError);
        }
    }
    private void RevertWorkspaceMetadata()
    {
        AbandonPendingPropertyStrings();
        documents.CommitAll();
        if (workspace?.HasPackageChanges != true || sessionHistory == null) return;
        sessionHistory.RevertToSavePoint();
        if (authoring != null) foreach (var state in authoring.PublishedStates)
            if (state.Document is PackageMetadataDocument metadata) metadata.TryReconcileSavedState();
        // Every caller immediately closes or replaces this workspace without saving.
        // Package/session dirty flags also include load-time upgrades and structural
        // notifications that remain set after undo. They do not prove that retained
        // editor history failed to restore. Release the remaining in-memory package
        // changes with the discarded session; do not force a save or mark them saved.
    }
    private void DisposeSessionAuthoring()
    {
        StopEffectLogs();
        if (sessionHistory == null) { documents.Configure(null); return; }
        StopEditorExtensions();
        sessionHistory.Clear();
        var errors = sessionHistory.Stack.RetryDiscardCleanup();
        if (errors.Count != 0)
        {
            foreach (var error in errors) outputLog.Append("Project", LogMessageType.Error, "History cleanup", error.ToString());
            throw new AggregateException("Authored history cleanup is incomplete. Retry closing the project.", errors);
        }
        ClearScriptSourceTopology();
        DisposeEditorExtensions();
        ClearCutSourceDirectories();
        ClearVisualScriptCutState();
        ClearVisualScriptGeneratedOwnership();
        sessionHistory.UiLibraryGraphs.DetachAll(); sessionHistory.AssetGraphs.DetachAll(); sessionHistory.HierarchyGraphs.DetachAll();
        ReleaseSolutionProjectOwnership();
        authoring!.Dispose(); sessionHistory.Dispose();
        sessionHistory = null; authoring = null; documents.Configure(null);
    }
    private bool IsReadOnlyAssetPath(string path) => workspace?.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), Path.GetFullPath(path)))?.Package.IsReadOnly == true;
    private void ReplaceAuthoredDocument(EditorDocumentEntry entry, Func<IEditorDocument> create, string name, bool validatePropertyEdits = true)
    {
        if (sessionHistory == null || authoring == null)
            throw new InvalidOperationException("The authored session is not available.");
        if (validatePropertyEdits) ValidatePendingPropertyStrings();
        documents.CommitAll();
        var before = entry.State ?? throw new InvalidOperationException("The document has no authored owner.");
        if (ReferenceEquals(documents.Active, entry)) ClearInheritedInspectorState();
        var next = create(); EditorAuthoringState after;
        try { after = authoring.RegisterUnpublished(next, before.AssetId); }
        catch { if (!authoring.Owns(next)) next.Dispose(); throw; }
        using var beforeLease = before.AcquireView(); using var afterLease = after.AcquireView();
        var applied = false; var recorded = false;
        try
        {
            if (!validatePropertyEdits) AbandonPendingPropertyStrings(before.Document);
            using var transaction = sessionHistory.CreateTransaction(name);
            using var propagated = sessionHistory.CaptureOperationsAtomic(() =>
            {
                ApplyAuthoredSwap(before, after); applied = true;
                sessionHistory.HierarchyGraphs.ReconcilePublishedBases(after.AssetId);
                sessionHistory.AssetGraphs.ReconcilePublished(after.AssetId);
                sessionHistory.UiLibraryGraphs.ReconcilePublished(after.AssetId);
            });
            var swap = new AuthoredSwapOperation(
                () => ApplyAuthoredSwapAndReconcile(after, before),
                () => ApplyAuthoredSwapAndReconcile(before, after));
            try
            {
                sessionHistory.PushOperation(new CompositeOperation(new Operation[] { swap }.Concat(propagated.Entries.Select(entry => entry.Operation))),
                    propagated.Participants.Concat(new[] { before, after }).Distinct().ToArray()); recorded = true;
            }
            catch { sessionHistory.RestoreFailedCapture(propagated); throw; }
        }
        catch
        {
            if (!recorded && applied)
            {
                ApplyAuthoredSwap(after, before);
            }
            if (!recorded) authoring.ReleaseIfUnused(after);
            throw;
        }
    }
    private void ApplyAuthoredSwapAndReconcile(EditorAuthoringState previous, EditorAuthoringState next)
    {
        ApplyAuthoredSwap(previous, next);
        // The real composite already replays the retained derived node changes.
        // Re-running reconciliation would clone references and create fresh part identities.
        if (sessionHistory!.Stack.RollInProgress || sessionHistory.IsCompensating) return;
        try { sessionHistory!.HierarchyGraphs.ReconcilePublishedBases(next.AssetId); sessionHistory.AssetGraphs.ReconcilePublished(next.AssetId); sessionHistory.UiLibraryGraphs.ReconcilePublished(next.AssetId); }
        catch (Exception failure)
        {
            try { ApplyAuthoredSwap(next, previous); sessionHistory!.HierarchyGraphs.ReconcilePublishedBases(previous.AssetId); sessionHistory.AssetGraphs.ReconcilePublished(previous.AssetId); sessionHistory.UiLibraryGraphs.ReconcilePublished(previous.AssetId); }
            catch (Exception recovery) { throw new AggregateException("Inherited graph replacement and restoration failed.", failure, recovery); }
            throw;
        }
    }
    private void ApplyAuthoredSwap(EditorAuthoringState previous, EditorAuthoringState next)
    {
        var entry = documents.Find(previous.Document);
        var active = ReferenceEquals(documents.Active, entry) && entry != null;
        Action? restoreDerivedPresentation = null;
        authoring!.Unpublish(previous);
        try
        {
            authoring.Publish(next);
            if (next.Document is VisualScriptDocument nextVisualScript) InitializeVisualScriptDocument(nextVisualScript);
            if (authoredGenerationBatchDepth == 0)
            { sessionHistory!.HierarchyGraphs.RefreshPublished(); sessionHistory.AssetGraphs.RefreshPublished(); sessionHistory.UiLibraryGraphs.RefreshPublished(); }
            if (entry != null) documents.ReplaceViewDocument(entry, next);
            if (authoredGenerationBatchDepth > 0)
            { authoredGenerationPresentation.Add((previous.Document, next.Document)); authoredGenerationViewPending |= active; }
            var replacedPresentation = authoredGenerationBatchDepth > 0 || ReplacePresentedHierarchyDocument(previous.Document, next.Document);
            if (!replacedPresentation && active && entry != null) ActivateDocument(entry);
            else if (!replacedPresentation && ReferenceEquals(document, previous.Document) && next.Document is EntityHierarchyDocument scene)
            { if (projectContent != null) { engineViewport!.Load(scene, workspace!, projectContent); RestoreRefreshedSceneModels(scene); } document = scene; inspectedEntity = null; }
            if (ReferenceEquals(assetDocument, previous.Document)) assetDocument = next.Document as ProceduralAssetDocument;
            if (!sessionHistory!.Stack.RollInProgress && !sessionHistory.IsCompensating)
                RefreshVisualScriptDerivedPresentations(next, out restoreDerivedPresentation);
        }
        catch
        {
            if (next.IsPublished) authoring.Unpublish(next);
            authoring.Publish(previous);
            restoreDerivedPresentation?.Invoke();
            if (authoredGenerationBatchDepth == 0)
            { sessionHistory!.HierarchyGraphs.RefreshPublished(); sessionHistory.AssetGraphs.RefreshPublished(); sessionHistory.UiLibraryGraphs.RefreshPublished(); }
            if (entry != null) documents.ReplaceViewDocument(entry, previous);
            if (authoredGenerationBatchDepth > 0)
            { authoredGenerationPresentation.Add((next.Document, previous.Document)); authoredGenerationViewPending |= active; }
            var restoredPresentation = authoredGenerationBatchDepth > 0 || ReplacePresentedHierarchyDocument(next.Document, previous.Document);
            if (!restoredPresentation && active && entry != null) ActivateDocument(entry);
            else if (!restoredPresentation && ReferenceEquals(document, next.Document) && previous.Document is EntityHierarchyDocument scene)
            { if (projectContent != null) { engineViewport!.Load(scene, workspace!, projectContent); RestoreRefreshedSceneModels(scene); } document = scene; }
            if (ReferenceEquals(assetDocument, next.Document)) assetDocument = previous.Document as ProceduralAssetDocument;
            throw;
        }
        RebindSkyboxSettingsDocument(previous.Document, next.Document);
        RebindSpriteSettingsDocument(previous.Document, next.Document);
        RebindFontSettingsDocument(previous.Document, next.Document);
        ReleaseTextureSettingsDocument(previous.Document);
        if (previous.Document is ScriptSourceDocument source) ReleaseScriptEditor(source);
        if (previous.Document is VisualScriptDocument visualScript) ReleaseVisualScriptView(visualScript);
        if (next.Document is IAssetTagDocument tagged) assetBrowser.UpdateAuthoredTags(tagged.AuthoredAsset.Id, tagged.AuthoredAsset.Tags);
    }
    private sealed class AuthoredSwapOperation(Action undo, Action redo) : Operation
    {
        protected override void Rollback() => undo();
        protected override void Rollforward() => redo();
    }
}
