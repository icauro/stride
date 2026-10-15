using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal readonly record struct HierarchyDropRowGeometry(float Indent, bool Open);

internal sealed partial class EditorGame
{
    private readonly record struct HierarchyEntityDropTarget(Entity? Parent, string Folder, int Index, bool Edge, bool After, float Indent, Entity? InsertItem);
    private object HierarchyDropRoot(EntityHierarchyDocument owner) => owner is SceneDocument scene ? scene
        : owner is PrefabDocument prefab ? HierarchyContentItem.Root(prefab)
        : throw new InvalidOperationException("The hierarchy root has no supported document owner.");
    private void RememberHierarchyDropRow(object row, bool open, float indent)
        => HierarchyController.State.DropRows[row] = new(indent, open);
    private static HierarchyContentItem HierarchyDropFolder(EntityHierarchyDocument owner, Guid? parent, string path)
        => HierarchyContentItem.Folder(owner, parent, owner.GetHierarchyFolders(parent)
            .FirstOrDefault(folder => string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)) ?? path);
    private object? HierarchyDropParent(object row)
    {
        if (row is SceneDocument scene)
        {
            var parent = SceneParentId(scene.Asset) is { } id ? authoring?.Find(id)?.Document as SceneDocument : null;
            return parent != null && HierarchyController.State.DropRows.ContainsKey(parent) ? parent : null;
        }
        if (row is not HierarchyContentItem item || item.IsRoot) return null;
        var owner = item.Document;
        if (item.FolderPath is { } folder)
        {
            var slash = folder.LastIndexOf('/');
            return slash >= 0 ? HierarchyDropFolder(owner, item.FolderOwner, folder[..slash])
                : item.FolderOwner is { } parent ? HierarchyContentItem.Entity(owner, parent) : HierarchyDropRoot(owner);
        }
        if (item.EntityId is not { } entity || !owner.Asset.Hierarchy.Parts.TryGetValue(entity, out var part)) return null;
        var parentId = part.Entity.Transform.Parent?.Entity.Id;
        var path = (part.Folder ?? "").Trim('/');
        return path.Length > 0 ? HierarchyDropFolder(owner, parentId, path)
            : parentId is { } parentEntity ? HierarchyContentItem.Entity(owner, parentEntity) : HierarchyDropRoot(owner);
    }
    private bool IsLastHierarchyDropChild(object child, object parent)
    {
        EntityHierarchyDocument owner;
        Guid? entityOwner;
        string path;
        if (parent is SceneDocument scene)
        {
            if (scene.Asset.ChildrenIds.Count > 0)
                return child is SceneDocument candidate && candidate.Asset.Id == scene.Asset.ChildrenIds[^1];
            owner = scene; entityOwner = null; path = "";
        }
        else if (parent is HierarchyContentItem item)
        {
            owner = item.Document; entityOwner = item.EntityId ?? item.FolderOwner; path = item.FolderPath ?? "";
        }
        else return false;
        var lastEntity = owner.HierarchyChildren(entityOwner).LastOrDefault(entity =>
            string.Equals((owner.Asset.Hierarchy.Parts[entity.Id].Folder ?? "").Trim('/'), path, StringComparison.OrdinalIgnoreCase));
        if (lastEntity != null) return child.Equals(HierarchyContentItem.Entity(owner, lastEntity.Id));
        var prefix = path.Length == 0 ? "" : path + "/";
        var lastFolder = owner.GetHierarchyFolders(entityOwner).Where(folder => folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !folder[prefix.Length..].Contains('/')).OrderBy(folder => folder[prefix.Length..], StringComparer.InvariantCultureIgnoreCase).LastOrDefault();
        return lastFolder != null && child.Equals(HierarchyContentItem.Folder(owner, entityOwner, lastFolder));
    }
    private bool HierarchyDropHasChildren(object row)
    {
        if (row is SceneDocument scene) return scene.Asset.Hierarchy.RootParts.Count > 0 || scene.Asset.ChildrenIds.Count > 0 || scene.GetHierarchyFolders(null).Count > 0;
        if (row is not HierarchyContentItem item) return false;
        if (item.FolderPath is { } folder) return item.Document.HierarchyFolderEntities(item.FolderOwner, folder).Length > 0
            || item.Document.GetHierarchyFolders(item.FolderOwner).Any(path => path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
        return item.EntityId is { } id ? item.Document.HierarchyChildren(id).Any() || item.Document.GetHierarchyFolders(id).Count > 0
            : item.Document.Asset.Hierarchy.RootParts.Count > 0 || item.Document.GetHierarchyFolders(null).Count > 0;
    }
    private object ResolveHierarchyAfterTarget(object row)
    {
        var target = row;
        var child = row;
        var seen = new HashSet<object>();
        while (seen.Add(child) && HierarchyDropParent(child) is { } parent && IsLastHierarchyDropChild(child, parent))
        {
            if (HierarchyController.State.DropRows.TryGetValue(target, out var geometry) && Gui.GetIO().MousePos.X < geometry.Indent)
                target = parent;
            child = parent;
        }
        return target;
    }
    private (bool Edge, bool After) HierarchyInsertBand(object row, bool emptyArea = false,
        System.Numerics.Vector2? minimum = null, System.Numerics.Vector2? maximum = null)
    {
        var min = minimum ?? Gui.GetItemRectMin(); var max = maximum ?? Gui.GetItemRectMax();
        var height = max.Y - min.Y; var y = Gui.GetIO().MousePos.Y - min.Y;
        if (!float.IsFinite(height) || !float.IsFinite(y) || height <= 0 || y < 0 || y > height) return (false, false);
        var dpi = Gui.GetStyle().FontScaleDpi;
        var band = emptyArea ? 4 * dpi : Math.Min(Math.Clamp(height * .3f, 3 * dpi, 10 * dpi), height * .4f);
        if (y < band) return (true, false);
        var expanded = HierarchyController.State.DropRows.TryGetValue(row, out var geometry) && geometry.Open && HierarchyDropHasChildren(row);
        return y > height - band && (!expanded || emptyArea) ? (true, true) : (false, false);
    }
    private HierarchyEntityDropTarget ResolveHierarchyEntityDrop(Entity? row, EntityHierarchyDocument owner)
    {
        var inside = InsideHierarchyEntityDrop(row, owner);
        if (row == null) return inside;
        var item = HierarchyContentItem.Entity(owner, row.Id);
        return ResolveHierarchyContentDrop(item, inside);
    }
    private HierarchyEntityDropTarget InsideHierarchyEntityDrop(Entity? row, EntityHierarchyDocument owner)
        => new(row, "", row?.Transform.Children.Count ?? owner.Asset.Hierarchy.RootParts.Count, false, false, Gui.GetItemRectMin().X, row);
    private HierarchyEntityDropTarget InsideHierarchyFolderDrop(EntityHierarchyDocument owner, Guid? parentId, string path)
    {
        var parent = parentId is { } id ? owner.Asset.Hierarchy.Parts[id].Entity : null;
        return new(parent, path, parent?.Transform.Children.Count ?? owner.Asset.Hierarchy.RootParts.Count,
            false, false, Gui.GetItemRectMin().X, null);
    }
    private HierarchyEntityDropTarget ResolveHierarchyFolderDrop(EntityHierarchyDocument owner, Guid? parentId, string path)
    {
        var inside = InsideHierarchyFolderDrop(owner, parentId, path);
        return ResolveHierarchyContentDrop(HierarchyContentItem.Folder(owner, parentId, path), inside);
    }
    private HierarchyEntityDropTarget ResolveHierarchyContentDrop(HierarchyContentItem item, HierarchyEntityDropTarget inside)
    {
        var owner = item.Document;
        var band = HierarchyInsertBand(item);
        if (!band.Edge) return inside;
        var resolved = band.After ? ResolveHierarchyAfterTarget(item) : item;
        // Native folders do not support insertion. Root boundaries are reserved for scene-root payloads.
        if (resolved is not HierarchyContentItem { EntityId: { } id } entityItem || !ReferenceEquals(entityItem.Document, owner)
            || !owner.Asset.Hierarchy.Parts.TryGetValue(id, out var design)) return inside;
        var target = design.Entity;
        var parent = target.Transform.Parent?.Entity;
        var index = (parent?.Transform.Children.IndexOf(target.Transform) ?? owner.Asset.Hierarchy.RootParts.IndexOf(target)) + (band.After ? 1 : 0);
        return new(parent, design.Folder ?? "", index, true, band.After,
            HierarchyController.State.DropRows.TryGetValue(resolved, out var geometry) ? geometry.Indent : inside.Indent, target);
    }
    private void DrawHierarchyInsertionCue(bool? after, float? indent = null)
    {
        var min = Gui.GetItemRectMin(); var max = Gui.GetItemRectMax();
        var color = Gui.GetColorU32(new System.Numerics.Vector4(.38f, .74f, .94f, 1));
        if (after is { } insertAfter)
        {
            var y = insertAfter ? max.Y : min.Y;
            Gui.GetWindowDrawList().AddLine(new System.Numerics.Vector2(indent ?? min.X, y), new(max.X, y), color, 2);
        }
        else Gui.GetWindowDrawList().AddRect(min, max, color);
    }
}
