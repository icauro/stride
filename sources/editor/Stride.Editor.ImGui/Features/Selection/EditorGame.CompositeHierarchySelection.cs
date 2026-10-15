using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorSelectionService? selectionService;
    private EditorSelectionService SelectionService => selectionService ??= new EditorSelectionService(new SelectionContext(this));
    private sealed class SelectionContext(EditorGame host) : IEditorSelectionContext
    {
        private EntityHierarchyDocument? cpuOwner;
        private EntityHierarchyDocument[] cpuOwners = [];
        private readonly List<EntityHierarchyDocument> propertyOwners = [];
        private readonly List<EntityHierarchyDocument> pendingPropertyOwners = [];
        private readonly List<SceneDocument> pendingScenes = [];
        private readonly HashSet<Stride.Core.Assets.AssetId> visitedScenes = [];
        private long propertyOwnerVersion;
        public IReadOnlyCollection<EntityHierarchyDocument>? PresentedOwners
        {
            get
            {
                if (!ReferenceEquals(cpuOwner, host.document))
                { cpuOwner = host.document; cpuOwners = cpuOwner == null ? [] : [cpuOwner]; }
                pendingPropertyOwners.Clear();
                pendingPropertyOwners.AddRange(host.engineViewport?.PresentedHierarchyDocuments ?? (IReadOnlyList<EntityHierarchyDocument>)cpuOwners);
                // The hierarchy represents authored scenes even when they have no loaded game-side scene.
                pendingScenes.Clear(); visitedScenes.Clear();
                if (host.PresentedHierarchyDocument is SceneDocument root) pendingScenes.Add(root);
                for (var index = 0; index < pendingScenes.Count; index++)
                {
                    var scene = pendingScenes[index];
                    if (!visitedScenes.Add(scene.Asset.Id) || !IsCurrentHierarchy(scene)) continue;
                    if (!pendingPropertyOwners.Contains(scene)) pendingPropertyOwners.Add(scene);
                    foreach (var childId in scene.Asset.ChildrenIds)
                        if (host.authoring?.Find(childId) is { IsPublished: true, Document: SceneDocument child }
                            && SceneParentId(child.Asset) == scene.Asset.Id) pendingScenes.Add(child);
                }
                if (!propertyOwners.SequenceEqual(pendingPropertyOwners))
                { propertyOwners.Clear(); propertyOwners.AddRange(pendingPropertyOwners); propertyOwnerVersion++; }
                return propertyOwners;
            }
        }
        public long PresentedOwnerVersion => propertyOwnerVersion;
        public long PresentationVersion => host.scenePresentationVersion;
        public EntityHierarchyDocument? ActiveHierarchy => host.document;
        public AssetBrowserState Assets => host.assetBrowser;
        public bool IsSelectable(EntityHierarchyDocument owner) => IsCurrentHierarchy(owner)
            && (owner is SceneDocument scene ? host.IsCurrentPresentedSceneRoot(scene)
                : ReferenceEquals(host.PresentedHierarchyDocument, owner) || ReferenceEquals(host.document, owner));
        public bool IsCurrentHierarchy(EntityHierarchyDocument owner) => host.authoring?.Find(owner.Asset.Id) is { IsPublished: true } state
            && ReferenceEquals(state.Document, owner) && ReferenceEquals(owner.SessionHistory, host.sessionHistory);
        public bool IsSceneRootAvailable(SceneDocument owner) => host.IsCurrentPresentedSceneRoot(owner);
        public void BeforeSelectionChange() => host.BeforeNativeSelectionChange();
        public void AfterSelectionChange() => host.NotifyNativeHierarchySelection();
        public IDisposable PreserveNativeSelectionIdentity() => host.nativeSelectionScopes.Suppress();
    }
    private (EntityHierarchyDocument Document, Guid Entity)? compositeHierarchyPrimary
    {
        get => SelectionService.PrimaryIdentity;
        set => SelectionService.PrimaryIdentity = value;
    }
    internal bool CanCopyHierarchySelection => SelectionService.CanCopy;
    internal bool CanDeleteHierarchySelection => !ContainsPresentedSceneRoot && (HasOnlySceneRootSelection
        ? CanRemoveSceneRoots(SelectionService.SelectedSceneRoots.ToArray()) : SelectionService.CanDelete);
    internal bool CanDuplicateHierarchySelection => SelectionService.CanDuplicate;
    internal IReadOnlyList<EntityHierarchyDocument> SelectedHierarchyDocuments => SelectionService.SelectedOwners;
    internal (EntityHierarchyDocument Document, Entity Entity)? CompositeHierarchyPrimary => SelectionService.Primary;
    private void ClearOtherHierarchySelection(EntityHierarchyDocument owner) => SelectionService.ClearOtherHierarchySelection(owner);
    private void SelectCompositeHierarchyEntity(EntityHierarchyDocument owner, Entity? entity, bool toggle)
    {
        selectedHierarchyFolder = null;
        SelectionService.SelectHierarchyEntity(owner, entity, toggle);
    }
}
