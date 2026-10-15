using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal Game? RuntimeGame => sceneAuthority?.ViewGame;
    internal EntityHierarchyDocument? PresentedDocument { get; private set; }
    private EntityHierarchyDocument? activeHierarchyDocument;
    private EntityHierarchyDocument? selectedHierarchyDocument;
    private IDisposable? presentedDocumentLease;
    private long selectionRuntimeVersion;
    private long selectedRuntimeVersion = -1;
    private readonly List<EntityHierarchyDocument> presentedHierarchyDocuments = [];
    internal IReadOnlyList<EntityHierarchyDocument> PresentedHierarchyDocuments => presentedHierarchyDocuments;
    internal long PresentedOwnerVersion { get; private set; }
    private readonly Dictionary<EntityHierarchyDocument, long> selectedOwnerVersions = [];
    private void RefreshPresentedHierarchyOwners()
    {
        PruneSceneOffsetAnchors();
        if (PresentedDocument == null) return;
        if (presentedHierarchyDocuments.Count == childScenes.Count + 1 && ReferenceEquals(presentedHierarchyDocuments[0], PresentedDocument)
            && childScenes.Values.All(entry => presentedHierarchyDocuments.Contains(entry.Document))) return;
        presentedHierarchyDocuments.Clear(); presentedHierarchyDocuments.Add(PresentedDocument);
        foreach (var child in childScenes.Values) presentedHierarchyDocuments.Add(child.Document);
        PresentedOwnerVersion++;
    }
    internal EntityHierarchyDocument? RuntimeOwner(Entity? entity) => entity == null ? null : childPickingOwners.GetValueOrDefault(entity)
        ?? (runtimeEntities.TryGetValue(entity.Id, out var root) && ReferenceEquals(root, entity) ? PresentedDocument : null);
    internal Entity? ResolveRuntimeEntity(EntityHierarchyDocument? owner, Guid id)
        => owner == null ? null : ReferenceEquals(owner, PresentedDocument) ? runtimeEntities.GetValueOrDefault(id)
        : childScenes.TryGetValue(owner.Asset.Id, out var child) && ReferenceEquals(child.Document, owner) ? child.Entities.GetValueOrDefault(id) : null;
    internal bool IsPresentedScene(EntityHierarchyDocument owner) => ReferenceEquals(owner, PresentedDocument)
        || childScenes.TryGetValue(owner.Asset.Id, out var child) && ReferenceEquals(child.Document, owner);
    private bool SynchronizePresentedRootLoading(EntityHierarchyDocument root)
    {
        var loaded = root is not SceneDocument scene || SceneLoaded?.Invoke(scene) != false;
        if (loaded) return true;
        foreach (var entity in runtimeEntities.Values)
            if (entity.Transform.Parent == null && ReferenceEquals(entity.Scene, loadedScene)) entity.Scene = null;
        synchronizedStructureVersion = -1;
        SynchronizeChildScenes(root); Camera.Component.Update(); Grid?.Update(); CameraOrientation?.Update();
        return false;
    }
    private void SynchronizeActiveSelection(EntityHierarchyDocument document)
    {
        var presentationVersion = ScenePresentationVersion?.Invoke() ?? 0;
        var changed = !ReferenceEquals(selectedHierarchyDocument, document) || selectedRuntimeVersion != selectionRuntimeVersion
            || selectedOwnerVersions.Count != presentedHierarchyDocuments.Count || selectedPresentationVersion != presentationVersion;
        foreach (var owner in presentedHierarchyDocuments)
        {
            if (!selectedOwnerVersions.TryGetValue(owner, out var version) || version != owner.SelectionVersion) changed = true;
        }
        if (!changed) return;
        selectedOwnerVersions.Clear();
        selectedModels.Clear();
        RefreshWorldSelection();
        foreach (var owner in presentedHierarchyDocuments)
            selectedOwnerVersions[owner] = owner.SelectionVersion;
        foreach (var entity in selectedWorldEntities)
            if (entity.Get<ModelComponent>()?.Model != null) selectedModels.Add(entity);
        selectedHierarchyDocument = document; synchronizedSelectionVersion = document.SelectionVersion;
        selectedRuntimeVersion = selectionRuntimeVersion;
        selectedPresentationVersion = presentationVersion;
        foreach (var feature in selectionFeatures) feature.NotifySelectionChanged();
    }
}
