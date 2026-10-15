namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly EditorDocumentSession documents = new();
    private EditorDocumentEntry? selectDocumentTab;
    private bool focusDocumentPanel;
    private EditorDocumentEntry? closingDocument;
    private bool openDocumentClose;
    private string? documentCloseError;
    private EditorDocumentEntry? pendingDocumentClose;
    private IEditorDocument? closingDocumentOwner, pendingDocumentCloseOwner;
    private IEditorDocument? CommandDocument => PropertyGridCommandDocument ?? documents.Active?.Document;
    private void UseSceneDocumentContext()
    {
        ClearAssetPropertyContext();
        assetDeletionCommandContext = false;
        assetRelocationCommandContext = false;
        packageFolderCommandContext = false;
        assetTagBatchCommandContext = false;
        assetCommandContext = false;
        if (documents.Find(document) is { } entry && !ReferenceEquals(documents.Active, entry))
        { documents.Activate(entry); selectDocumentTab = entry; }
    }

    private void ActivateDocument(EditorDocumentEntry entry)
    {
        if (!IsGraphicsRecoveryCpuClosing && !ReferenceEquals(documents.Active, entry)) ValidatePendingPropertyStrings();
        CancelSceneCameraNavigation();
        if (!ReferenceEquals(documents.Active, entry)) CancelSceneLightProbeCommands();
        if (IsGraphicsRecoveryCpuClosing)
        {
            documents.Activate(entry);
            if (entry.Document is EntityHierarchyDocument) ClearCpuHierarchyPresentation();
            document = entry.Document as EntityHierarchyDocument;
            assetDocument = entry.Document as ProceduralAssetDocument;
            selectedAssetPath = entry.Path; selectDocumentTab = entry;
            assetCommandContext = entry.Document is ProceduralAssetDocument;
            inspectedEntity = null; componentErrors.Clear(); activeColorPopup = null;
            return;
        }
        if (!ReferenceEquals(documents.Active, entry)) ClearInheritedInspectorState();
        assetDeletionCommandContext = false;
        if (!ReferenceEquals(documents.Active, entry)) documents.Active?.Document.CommitEdits();
        assetRelocationCommandContext = false;
        packageFolderCommandContext = false;
        assetTagBatchCommandContext = false;
        FinishEntityRename(true); document?.EndEdits(); assetDocument?.EndEdit();
        if (entry.Document is EntityHierarchyDocument scene
            && (!ReferenceEquals(document, scene) || !ReferenceEquals(engineViewport?.PresentedDocument, scene)))
        {
            CaptureSceneDocumentControlState();
            SaveSceneCameraSettings();
            if (projectContent != null) engineViewport = EnsureSceneDocumentView(entry).Viewport;
            else engineViewport = null;
            ClearCpuHierarchyPresentation();
            document = scene; componentErrors.Clear(); activeColorPopup = null;
            inspectedEntity = null; RestoreSceneDocumentControlState(entry);
        }
        if (entry.Document is ProceduralAssetDocument asset)
        { assetDocument = asset; assetEditError = null; }
        documents.Activate(entry);
        assetCommandContext = entry.Document is ProceduralAssetDocument;
        selectedAssetPath = entry.Path; selectDocumentTab = entry; focusDocumentPanel = true;
        statusMessage = "Editing " + entry.Name;
    }
    private void RestoreRefreshedSceneModels(EntityHierarchyDocument? target = null)
    {
        foreach (var resource in refreshedModels) engineViewport!.ReplaceRuntimeModel(resource.Url, resource.Model);
        foreach (var resource in refreshedSceneTextures)
            engineViewport!.ReplaceRuntimeTexture(resource.Key, resource.Value.Texture, target ?? document);
        ReplayCompiledSpriteSheets(engineViewport!, target ?? document);
        foreach (var installed in installedSkyboxes.Values)
            if (TryGetRefreshedSkybox(installed.Url, out var skybox))
                engineViewport!.ReplaceRuntimeSkybox(installed.Url, skybox, target ?? document);
    }
    private void OpenRetainedDocument(string path, bool scene, bool reload, bool prefab = false)
    {
        var entry = documents.Find(path);
        if (entry != null && !reload) { ActivateDocument(entry); return; }
        IEditorDocument Create() => prefab ? new PrefabDocument(path, sessionHistory, IsReadOnlyAssetPath(path))
            : scene ? new SceneDocument(path, sessionHistory, IsReadOnlyAssetPath(path)) : new ProceduralAssetDocument(path, sessionHistory, IsReadOnlyAssetPath(path));
        var next = GetCanonicalDocument(path, Create);
        if (entry == null)
        {
            entry = documents.Add(next);
            try { ActivateDocument(entry); }
            catch { documents.Remove(entry); throw; }
        }
        else
        {
            ReplaceAuthoredDocument(entry, Create, "Reload " + entry.Name);
        }
    }
    private void RequestDocumentClose(EditorDocumentEntry entry, bool fromBatch = false)
    {
        if (!fromBatch) CancelDocumentCloseBatch(clearCurrent: true);
        var owner = entry.Document;
        if (!documents.Entries.Contains(entry)) return;
        ValidatePendingPropertyStrings();
        CancelSceneCameraNavigation();
        CancelSceneLightProbeCommands();
        if (!documents.Entries.Contains(entry) || !ReferenceEquals(entry.Document, owner))
            throw new InvalidOperationException("The document changed while preparing its close request.");
        FinishEntityRename(true); owner.CommitEdits();
        if (!documents.Entries.Contains(entry) || !ReferenceEquals(entry.Document, owner))
            throw new InvalidOperationException("The document changed while preparing its close request.");
        if (entry.Document.IsDirty) { closingDocument = entry; closingDocumentOwner = owner; documentCloseError = null; openDocumentClose = true; }
        else { pendingDocumentClose = entry; pendingDocumentCloseOwner = owner; }
    }
    private void CloseDocument(EditorDocumentEntry entry)
    {
        if (entry.Document is UIDocument ui) ReleaseUIEditorDocument(ui);
        ReleaseNonSceneDocumentPresentation(entry);
        CancelSceneCameraNavigation();
        CancelSceneLightProbeCommands();
        var wasActive = ReferenceEquals(documents.Active, entry);
        var closedPresentedScene = ReferenceEquals(engineViewport?.PresentedDocument, entry.Document);
        if (closedPresentedScene) SaveSceneCameraSettings();
        ReleaseSceneDocumentView(entry);
        if (wasActive) ClearInheritedInspectorState();
        if (ReferenceEquals(document, entry.Document) || ReferenceEquals(engineViewport?.PresentedDocument, entry.Document))
        { SaveSceneCameraSettings(); engineViewport?.ClearScene(); document = null; inspectedEntity = null; componentErrors.Clear(); activeColorPopup = null; }
        if (ReferenceEquals(assetDocument, entry.Document))
        { assetDocument = null; assetEditorFocused = false; assetEditError = null; }
        ReleaseAssetTagDocument(entry.Document);
        ReleaseTextureSettingsDocument(entry.Document);
        if (entry.Document is ScriptSourceDocument source) ReleaseScriptEditor(source);
        if (entry.Document is VisualScriptDocument visualScript) ReleaseVisualScriptView(visualScript);
        if (entry.Document is IAssetTagDocument) assetTagBatchCommandContext = false;
        var next = documents.Remove(entry);
        if (ReferenceEquals(failedSceneDocument, entry.Document) || closedPresentedScene) ClearSceneSynchronizationFailure();
        if ((wasActive || closedPresentedScene) && next != null) ActivateDocument(next);
        else if (documents.Active == null) { assetCommandContext = false; selectedAssetPath = null; }
    }
    private void ClearDocumentSession()
    {
        if (IsPackagePlatformUpdating)
            throw new InvalidOperationException("Platform update readers must finish retiring before the authoring session is released.");
        ValidatePendingPropertyStrings();
        ClearSceneRootDragState();
        ClearNonSceneDocumentPresentations();
        CancelSceneCameraNavigation();
        ClearPropertyPathDropTargets();
        ClearContentReferencePicker();
        CancelSceneLightProbeCommands();
        ClearDebugWindow();
        ClearAssetRepair();
        ClearExternalAssetChanges();
        ClearVisualScriptViews();
        ClearInheritedInspectorState();
        ClearScriptEditors();
        documents.CommitAll();
        if (editorGraphicsRecovery is { } recovery) ClearSceneDocumentGraphicsRecovery(recovery);
        ClearWorkspaceTransitionViews();
        ClearDocumentGroupPresentation();
        ClearDocumentCloseRequests(); documents.Dispose();
        ClearSceneSynchronizationFailure();
        ClearDocumentReplacementState();
        sceneLoadedOverrides.Clear(); lockedScenes.Clear(); scenePresentationVersion++;
        lockedHierarchyEntities.Clear(); hierarchyExpansionApplied.Clear(); hierarchyExpandAll = null;
        ClearSelectableTextViews();
        foreignSceneRows.Clear();
        selectionService?.Reset();
        ClearCpuHierarchyPresentation();
        document = null; assetDocument = null; closingDocument = selectDocumentTab = pendingDocumentClose = null;
        closingDocumentOwner = pendingDocumentCloseOwner = null;
        activateAfterAssetChange = null;
        assetEditorFocused = assetCommandContext = false;
        openDocumentClose = false; documentCloseError = null;
        focusDocumentPanel = false;
        DisposeSessionAuthoring();
        ClearAssetDeletionState(); deletedDocuments.Clear(); activateAfterAssetChange = null;
        ClearAssetRelocationState(); assetTagBatchHistory.Clear(); assetTagBatchCommandContext = false;
    }
    private void DiscardDocumentChanges(EditorDocumentEntry entry)
    {
        var path = entry.Path;
        ReplaceAuthoredDocument(entry, () => entry.Document switch
        {
            PrefabDocument => new PrefabDocument(path, sessionHistory, IsReadOnlyAssetPath(path)),
            SceneDocument => new SceneDocument(path, sessionHistory, IsReadOnlyAssetPath(path)),
            ProceduralAssetDocument => new ProceduralAssetDocument(path, sessionHistory, IsReadOnlyAssetPath(path)),
            VisualScriptDocument => new VisualScriptDocument(path, sessionHistory!, IsReadOnlyAssetPath(path)),
            UIDocument => new UIDocument(path, sessionHistory!, IsReadOnlyAssetPath(path)),
            AssetMetadataDocument => new AssetMetadataDocument(path, sessionHistory, IsReadOnlyAssetPath(path)),
            ScriptSourceDocument => CreateScriptDocument(path),
            _ => throw new InvalidOperationException("This document cannot be reloaded."),
        }, "Discard changes: " + entry.Name, validatePropertyEdits: false);
    }
}
