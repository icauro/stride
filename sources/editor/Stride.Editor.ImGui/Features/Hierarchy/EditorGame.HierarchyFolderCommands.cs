using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void MoveHierarchyEntitiesToFolder(EntityHierarchyDocument owner, Entity[] entities, Guid? parent, string path, bool keepWorld)
    {
        if (IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(owner) || entities.Length == 0 || !owner.CanMoveEntitiesToFolder(entities, parent)
            || path.Length > 0 && !owner.GetHierarchyFolders(parent).Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        if (entities.All(entity => entity.Transform.Parent?.Entity.Id == parent && owner.Asset.Hierarchy.Parts[entity.Id].Folder == path)) return;
        ValidatePendingPropertyStrings();
        if (!IsCurrentHierarchyContextOwner(owner) || !owner.CanMoveEntitiesToFolder(entities, parent)
            || path.Length > 0 && !owner.GetHierarchyFolders(parent).Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        owner.MoveEntitiesToFolder(entities, parent, path, keepWorld);
    }
    private void MoveHierarchySelectionToFolder(Guid? parent, string path)
    {
        if (document == null || !document.CanMoveSelectionToFolder(parent)) return;
        MoveHierarchyEntitiesToFolder(document, document.GetSelectedTransformRoots(), parent, path, false);
    }
}
