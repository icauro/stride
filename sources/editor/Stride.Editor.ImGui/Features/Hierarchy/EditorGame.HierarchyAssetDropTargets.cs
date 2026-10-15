using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly record struct HierarchyAssetDropTarget(EntityHierarchyDocument Document, Guid? Parent, string Folder, int Index,
        bool RootAdd, bool? After = null, float? Indent = null, object? Boundary = null);
    private HierarchyAssetDropTarget InsideHierarchyAssetDrop(EntityHierarchyDocument owner, HierarchyEntityDropTarget inside, bool rootAdd = false)
        => new(owner, inside.Parent?.Id, inside.Folder, inside.Index, rootAdd);
    private bool TryHierarchyAssetBoundary(object row, bool after, out HierarchyAssetDropTarget result)
    {
        result = default;
        if (row is SceneDocument scene)
        {
            if (!ReferenceEquals(authoring?.Find(scene.Asset.Id)?.Document, scene)
                || SceneParentId(scene.Asset) is not { } id || authoring?.Find(id)?.Document is not SceneDocument parent
                || !IsCurrentHierarchyContextOwner(parent)) return false;
            var index = parent.Asset.ChildrenIds.IndexOf(scene.Asset.Id);
            if (index < 0) return false;
            result = new(parent, null, "", parent.Asset.Hierarchy.RootParts.Count + index + (after ? 1 : 0), false, after,
                HierarchyController.State.DropRows.TryGetValue(row, out var geometry) ? geometry.Indent : null, row);
            return true;
        }
        if (row is not HierarchyContentItem { EntityId: { } entityId } item
            || !ReferenceEquals(authoring?.Find(item.Document.Asset.Id)?.Document, item.Document)
            || !item.Document.Asset.Hierarchy.Parts.TryGetValue(entityId, out var part)) return false;
        var entity = part.Entity; var owner = entity.Transform.Parent?.Entity;
        var entityIndex = owner?.Transform.Children.IndexOf(entity.Transform) ?? item.Document.Asset.Hierarchy.RootParts.IndexOf(entity);
        if (entityIndex < 0) return false;
        result = new(item.Document, owner?.Id, part.Folder ?? "", entityIndex + (after ? 1 : 0), false, after,
            HierarchyController.State.DropRows.TryGetValue(row, out var entityGeometry) ? entityGeometry.Indent : null, row);
        return true;
    }
    private HierarchyAssetDropTarget ResolveHierarchyAssetTarget(object row, HierarchyAssetDropTarget inside, bool emptyArea = false)
    {
        var band = HierarchyInsertBand(row, emptyArea);
        if (!band.Edge) return inside;
        var resolved = band.After && !emptyArea ? ResolveHierarchyAfterTarget(row) : row;
        if (resolved is SceneDocument && HierarchyDropParent(resolved) == null) return inside;
        return TryHierarchyAssetBoundary(resolved, band.After, out var insertion) ? insertion : inside;
    }
}
