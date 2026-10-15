namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Stride.Core.Assets.AssetId? cpuPresentedHierarchyId;
    private readonly HashSet<Stride.Core.Assets.AssetId> cpuPresentationPath = [];
    private void ClearCpuHierarchyPresentation() { cpuPresentedHierarchyId = null; cpuPresentationPath.Clear(); }
    private EntityHierarchyDocument? PresentedHierarchyDocument
    {
        get
        {
            if (engineViewport?.PresentedDocument is { } runtimeRoot) return runtimeRoot;
            if (cpuPresentedHierarchyId is { } id && authoring?.Find(id) is { IsPublished: true, Document: SceneDocument root }
                && documents.Find(root) != null && document is SceneDocument current)
            {
                cpuPresentationPath.Clear();
                while (cpuPresentationPath.Add(current.Asset.Id))
                {
                    if (ReferenceEquals(current, root)) return root;
                    if (SceneParentId(current.Asset) is not { } parent
                        || authoring.Find(parent) is not { IsPublished: true, Document: SceneDocument next }
                        || !next.Asset.ChildrenIds.Contains(current.Asset.Id)) break;
                    current = next;
                }
            }
            return document;
        }
    }
    private bool ReplacePresentedHierarchyDocument(IEditorDocument previous, IEditorDocument next)
    {
        if (IsGraphicsRecoveryCpuClosing && previous is EntityHierarchyDocument && next is EntityHierarchyDocument cpuNext)
        {
            if (ReferenceEquals(document, previous)) document = cpuNext;
            inspectedEntity = null; componentErrors.Clear(); activeColorPopup = null;
            
            return true;
        }
        if (previous is not EntityHierarchyDocument before || next is not EntityHierarchyDocument after) return false;
        var wasActive = ReferenceEquals(document, before);
        if (projectContent != null && RebindPresentedSceneDocumentViews(before, after))
        {
            if (wasActive) document = after;
            inspectedEntity = null; componentErrors.Clear(); activeColorPopup = null; ClearInheritedInspectorState();
            return true;
        }
        if (engineViewport == null) return false;
        if (projectContent == null)
        {
            if (!wasActive) return false;
            document = after; inspectedEntity = null; componentErrors.Clear(); activeColorPopup = null;
            ClearInheritedInspectorState();
            return true;
        }
        if (ReferenceEquals(engineViewport.PresentedDocument, before))
        {
            var camera = engineViewport.Camera.Capture(showSceneSelection);
            var grid = engineViewport.Grid?.Settings;
            engineViewport.Load(after, workspace!, projectContent!);
            RestoreRefreshedSceneModels(after);
            engineViewport.Camera.Restore(camera);
            if (grid is { } settings) engineViewport.Grid?.ApplySettings(settings);
            if (wasActive) document = after;
            if (document != null && !(document is SceneDocument presentedActiveScene ? IsCurrentPresentedSceneRoot(presentedActiveScene)
                : engineViewport.IsPresentedScene(document)))
            {
                document = after;
                documents.Activate(documents.Find(after) ?? documents.Add(after));
            }
            engineViewport.Synchronize(document ?? after);
        }
        else if (wasActive && (engineViewport.IsPresentedScene(before)
            || after is SceneDocument refreshedActiveScene && IsCurrentPresentedSceneRoot(refreshedActiveScene)))
        {
            document = after;
            engineViewport.Synchronize(after);
        }
        else return false;
        inspectedEntity = null; componentErrors.Clear(); activeColorPopup = null; ClearInheritedInspectorState();
        return true;
    }
    private bool TryRestoreSceneHierarchyNavigation(SceneDocument scene)
    {
        if (!IsCurrentPresentedSceneRoot(scene)) return false;
        ActivateSceneRoot(scene, allowUnloaded: true, restoringNavigation: true);
        return true;
    }
    private void ActivateSceneRoot(SceneDocument next, bool allowUnloaded = false, bool restoringNavigation = false)
    {
        if (ReferenceEquals(next, document)) return;
        if (IsAuthoringPipelineUpdating || sessionHistory?.Registry.Find(next.Asset.Id) is not { IsPublished: true } state
            || !ReferenceEquals(state.Document, next))
            throw new InvalidOperationException("The active scene owner is no longer available.");
        if (allowUnloaded ? !IsCurrentPresentedSceneRoot(next)
            : (!IsSceneLoaded(next) && !ReferenceEquals(next, engineViewport?.PresentedDocument)) || engineViewport?.IsPresentedScene(next) != true)
            throw new InvalidOperationException("The scene is not loaded in this editor.");
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); document?.EndEdits(); assetDocument?.EndEdit();
        if (!IsCurrentPresentedSceneRoot(next) || !allowUnloaded && ((!IsSceneLoaded(next)
            && !ReferenceEquals(next, engineViewport?.PresentedDocument)) || engineViewport?.IsPresentedScene(next) != true)
            || allowUnloaded && !restoringNavigation && !ReferenceEquals(SelectionService.LastHierarchySelectedContent, next))
            throw new InvalidOperationException("The active scene owner changed.");
        engineViewport?.Gizmos?.Cancel(); engineViewport?.Picker?.Cancel("Active scene changed");
        var cpuRoot = engineViewport?.PresentedDocument == null ? PresentedHierarchyDocument as SceneDocument : null;
        var entry = documents.Find(next) ?? documents.Add(next);
        documents.Activate(entry);
        if (cpuRoot != null && documents.Find(cpuRoot) != null) cpuPresentedHierarchyId = cpuRoot.Asset.Id;
        document = next;
        componentErrors.Clear(); activeColorPopup = null; inspectedEntity = null; 
        ClearInheritedInspectorState();
        UseSceneDocumentContext(); assetCommandContext = false; selectDocumentTab = entry;
        engineViewport?.Synchronize(next);
    }
}
