namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanBeginHierarchyRowDrag(HierarchyContentItem row)
    {
        var current = SelectionService.HierarchyContent;
        HierarchyContentItem[] selected = SelectionService.IsContentSelected(row) ? current.ToArray() : [row];
        if (SelectionService.IsContentSelected(row) && SelectionService.SelectedSceneRoots.Count > 0 || selected.Any(item => item.IsRoot)) return false;
        foreach (var item in selected)
        {
            if (item.EntityId is { } id && item.Document.Asset.Hierarchy.Parts.TryGetValue(id, out var part)
                && IsInlineHierarchyEntityRenaming(item.Document, part.Entity)
                || item.FolderPath is { } path && IsRenamingHierarchyFolder(item.Document, item.FolderOwner, path)) return false;
            if (selected.Any(parent => IsHierarchyContentAncestor(parent, item))) return false;
        }
        return true;
    }
    private static bool IsHierarchyContentAncestor(HierarchyContentItem parent, HierarchyContentItem child)
    {
        if (parent == child || !ReferenceEquals(parent.Document, child.Document)) return false;
        var owner = child.Document;
        if (parent.FolderPath is { } path && parent.FolderOwner == child.FolderOwner && child.FolderPath is { } childPath
            && childPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) return true;
        var id = child.EntityId ?? child.FolderOwner;
        if (id is not { } entityId || !owner.Asset.Hierarchy.Parts.TryGetValue(entityId, out var part)) return false;
        var entity = child.EntityId != null && parent.EntityId != null ? part.Entity.Transform.Parent?.Entity : part.Entity;
        for (var depth = 0; entity != null && depth < owner.Asset.Hierarchy.Parts.Count; depth++, entity = entity.Transform.Parent?.Entity)
        {
            if (parent.EntityId == entity.Id) return true;
            if (parent.FolderPath is { } folder && entity.Transform.Parent?.Entity.Id == parent.FolderOwner
                && owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design)
                && (string.Equals(design.Folder, folder, StringComparison.OrdinalIgnoreCase)
                    || (design.Folder ?? "").StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }
    private sealed record HierarchyEntityDragSelection(EntityHierarchyDocument Source, Guid Entity,
        DragOwnerSelection[] Owners, EntityHierarchyDocument[] Documents);
    private HierarchyEntityDragSelection? activeHierarchyEntityDrag;
    private static DragOwnerSelection CaptureHierarchyDragOwner(EntityHierarchyDocument owner)
        => new(owner, owner.SnapshotSelection(), owner.SelectionVersion, owner.StructureVersion, owner.FolderVersion);
    private bool IsCurrentHierarchyDragOwner(DragOwnerSelection owner)
        => IsCurrentHierarchyDragSource(owner.Document) && owner.Document.SelectionVersion == owner.SelectionVersion
            && owner.Document.StructureVersion == owner.StructureVersion && owner.Document.FolderVersion == owner.FolderVersion;
    private void BeginHierarchyEntityDrag(EntityHierarchyDocument source, Guid entity)
    {
        if (activeHierarchyEntityDrag is { } existing && ReferenceEquals(existing.Source, source) && existing.Entity == entity) return;
        EntityHierarchyDocument[] owners = source.IsSelected(entity) ? SelectedHierarchyDocuments.ToArray() : [source];
        if (owners.Length == 0) owners = [source];
        activeHierarchyEntityDrag = new(source, entity, owners.Select(CaptureHierarchyDragOwner).ToArray(), owners);
    }
    private bool TryGetHierarchyEntityDrag(EntityHierarchyDocument source, Guid entity, out HierarchyEntityDragSelection selection)
    {
        if (activeHierarchyEntityDrag is { } captured && ReferenceEquals(captured.Source, source) && captured.Entity == entity)
        {
            selection = captured;
            return captured.Owners.Length > 0 && captured.Owners.All(IsCurrentHierarchyDragOwner);
        }
        // Legacy diagnostic payloads carry the actual source document identity but no live gesture certificate.
        selection = new(source, entity, [CaptureHierarchyDragOwner(source)], [source]);
        return IsCurrentHierarchyDragSource(source);
    }
}
