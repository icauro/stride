using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsCurrentPresentedSceneRoot(SceneDocument scene)
    {
        if (authoring?.Find(scene.Asset.Id) is not { IsPublished: true } state || !ReferenceEquals(state.Document, scene)) return false;
        if (PresentedHierarchyDocument is not SceneDocument root) return false;
        var visited = new HashSet<Stride.Core.Assets.AssetId>();
        while (visited.Add(scene.Asset.Id))
        {
            if (ReferenceEquals(scene, root)) return true;
            if (SceneParentId(scene.Asset) is not { } parent || authoring.Find(parent)?.Document is not SceneDocument next
                || !next.Asset.ChildrenIds.Contains(scene.Asset.Id)) return false;
            scene = next;
        }
        return false;
    }
    private bool SceneRootCoversOwner(SceneDocument root, EntityHierarchyDocument owner)
    {
        if (owner is not SceneDocument scene) return false;
        var visited = new HashSet<Stride.Core.Assets.AssetId>();
        while (visited.Add(scene.Asset.Id))
        {
            if (ReferenceEquals(scene, root)) return true;
            if (SceneParentId(scene.Asset) is not { } parent || ResolveSceneHierarchyDocument(parent) is not { } next) return false;
            scene = next;
        }
        throw new InvalidOperationException("Circular scene hierarchy");
    }
    private SceneDocument[] SelectedSceneOffsetTransformRoots()
    {
        var candidates = SelectionService.SelectedSceneRoots.Where(scene => IsSceneLoaded(scene) && !IsSceneLocked(scene)
            && ReferenceEquals(authoring?.Find(scene.Asset.Id)?.Document, scene)
            && engineViewport?.IsPresentedScene(scene) == true).ToArray();
        return candidates.Where(scene => !candidates.Any(parent => !ReferenceEquals(parent, scene) && SceneRootCoversOwner(parent, scene))).ToArray();
    }
    private Entity[] SceneGizmoEntityRoots(EntityHierarchyDocument owner)
    {
        if (engineViewport == null || SelectedSceneOffsetTransformRoots().Any(scene => SceneRootCoversOwner(scene, owner))) return [];
        return engineViewport.GetWorldTransformRoots(owner);
    }
    private void BindSceneRootTransformTargets(SceneTransformGizmos gizmos, EngineViewport viewport)
    {
        gizmos.SelectionSceneRoots = SelectedSceneOffsetTransformRoots;
        gizmos.SelectionSceneRootPrimary = () => SelectionService.PrimarySceneRootDocument;
        gizmos.SelectionContentVersion = () => SelectionService.ContentVersion;
        gizmos.BeforeGesture = ValidatePendingPropertyStrings;
        gizmos.ResolveSceneOffsetTarget = viewport.ResolveSceneOffsetTarget;
        gizmos.SelectionRoots = SceneGizmoEntityRoots;
    }
}
