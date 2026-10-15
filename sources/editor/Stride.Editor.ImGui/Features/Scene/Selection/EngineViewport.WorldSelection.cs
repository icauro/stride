using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private readonly HashSet<Entity> eligibleWorldSelection = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entity> selectedWorldRoots = [];
    private readonly HashSet<Entity> selectedWorldEntities = new(ReferenceEqualityComparer.Instance);
    private long selectedPresentationVersion = -1;

    private void RefreshWorldSelection()
    {
        eligibleWorldSelection.Clear(); selectedWorldRoots.Clear(); selectedWorldEntities.Clear();
        foreach (var owner in presentedHierarchyDocuments)
            foreach (var id in owner.SelectedIds)
                if (ResolveRuntimeEntity(owner, id) is { } entity && CanSelectSceneEntity(entity))
                    eligibleWorldSelection.Add(entity);

        foreach (var owner in presentedHierarchyDocuments)
        foreach (var id in owner.SelectedIds)
        {
            var entity = ResolveRuntimeEntity(owner, id);
            if (entity == null || !eligibleWorldSelection.Contains(entity)) continue;
            var selectedAncestor = false;
            for (var parent = entity.Transform.Parent; parent != null; parent = parent.Parent)
                if (eligibleWorldSelection.Contains(parent.Entity)) { selectedAncestor = true; break; }
            if (!selectedAncestor) selectedWorldRoots.Add(entity);
        }

        void Include(Entity entity)
        {
            if (!selectedWorldEntities.Add(entity)) return;
            foreach (var child in entity.Transform.Children) Include(child.Entity);
        }
        // Original SelectionUpdated publishes normalized selectable roots; the
        // mesh/component gizmo services then include their complete descendants.
        foreach (var entity in selectedWorldRoots) Include(entity);
    }

    private bool IsWorldSelectionEntity(Entity entity) => selectedWorldEntities.Contains(entity);

    internal Entity[] GetWorldTransformRoots(EntityHierarchyDocument owner)
    {
        SynchronizeActiveSelection(activeHierarchyDocument ?? PresentedDocument ?? owner);
        return selectedWorldRoots.Where(entity => ReferenceEquals(RuntimeOwner(entity), owner))
            .Select(entity => owner.Asset.Hierarchy.Parts[entity.Id].Entity).ToArray();
    }

    internal bool IsExplicitWorldSelection(EntityHierarchyDocument owner, Guid id)
        => ResolveRuntimeEntity(owner, id) is { } entity && eligibleWorldSelection.Contains(entity);

    private void ClearWorldSelection()
    {
        eligibleWorldSelection.Clear(); selectedWorldRoots.Clear(); selectedWorldEntities.Clear();
        selectedPresentationVersion = -1;
    }
}
