using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object? WorkflowHierarchyCreationEligibility { get; private set; }
    private readonly record struct HierarchyCreationDestination(EntityHierarchyDocument Owner, Guid? Parent, string Folder, object? SelectedContent);
    private HierarchyCreationDestination? HierarchySelectionCreationDestination()
    {
        var first = SelectionService.FirstHierarchySelectedContent;
        if (first is SceneDocument scene) return new HierarchyCreationDestination(scene, null, "", scene);
        if (first is HierarchyContentItem item) return new HierarchyCreationDestination(item.Document, item.EntityId ?? item.FolderOwner,
            item.FolderPath ?? "", item);
        return document == null ? null : new HierarchyCreationDestination(document, null, "", null);
    }
    private bool IsCurrentHierarchyCreationDestination(HierarchyCreationDestination target)
        => IsCurrentHierarchyContextOwner(target.Owner) && HierarchySelectionCreationDestination() == target
            && (target.Parent == null || target.Owner.Asset.Hierarchy.Parts.ContainsKey(target.Parent.Value))
            && (target.Folder.Length == 0 || target.Owner.GetHierarchyFolders(target.Parent).Contains(target.Folder, StringComparer.OrdinalIgnoreCase));
    private bool CanCreateInHierarchySelection(bool folder = false)
    {
        var busy = IsAuthoringPipelineUpdating;
        var destination = HierarchySelectionCreationDestination();
        var current = destination is { } candidate && IsCurrentHierarchyCreationDestination(candidate);
        if (busy || destination is not { } target || !current)
        {
            if (Controls.Enabled) WorkflowHierarchyCreationEligibility = new { busy, current, folder,
                ownerId = destination?.Owner.Asset.Id.ToString(), parentId = destination?.Parent,
                folderPath = destination?.Folder, source = destination?.SelectedContent?.GetType().Name, canCreate = false };
            return false;
        }
        var parent = target.Parent is { } id ? target.Owner.Asset.Hierarchy.Parts[id].Entity : null;
        var allowed = folder ? target.Owner.CanEditHierarchyFolders(target.Parent) : target.Owner.CanCreateEntity(parent);
        if (Controls.Enabled) WorkflowHierarchyCreationEligibility = new { busy, current, folder,
            ownerId = target.Owner.Asset.Id.ToString(), parentId = target.Parent,
            folderPath = target.Folder, source = target.SelectedContent?.GetType().Name, canCreate = allowed };
        return allowed;
    }
    private void CreateHierarchyEntityInSelection(Func<Entity> factory)
    {
        if (!CanCreateInHierarchySelection() || HierarchySelectionCreationDestination() is not { } target) return;
        QueueHierarchyContextCreation(target.Owner, target.Parent, target.Folder,
            () => InsertContextEntity(target.Owner, target.Parent, target.Folder, factory()), target);
    }
    private void CreateHierarchyFolderInSelection()
    {
        if (!CanCreateInHierarchySelection(true) || HierarchySelectionCreationDestination() is not { } target) return;
        QueueHierarchyContextCreation(target.Owner, target.Parent, target.Folder,
            () => CreateHierarchyFolderUi(target.Owner, target.Parent, target.Folder), target);
    }
}
