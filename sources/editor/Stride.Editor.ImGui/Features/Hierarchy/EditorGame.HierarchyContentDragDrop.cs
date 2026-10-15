using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string HierarchyContentDragType = "STRIDE_HIERARCHY_CONTENT";
    private struct HierarchyContentDragPayload { internal Guid Token; }
    private sealed record ContentDragOwner(EntityHierarchyDocument Document, long Structure, long Folder, long Selection);
    private sealed record ContentDrag(Guid Token, long SelectionVersion, HierarchyContentItem[] Items, ContentDragOwner[] Owners);
    private sealed record ContentDrop(ContentDrag Source, EntityHierarchyDocument Destination, Guid? Parent, string Folder, int Index, bool KeepWorld, bool Insertion = false);
    private ContentDrag? activeHierarchyContentDrag;
    private ContentDrop? deliveredHierarchyContentDrop;

    private bool HasMixedHierarchyDrag => SelectionService.HierarchyContent.Count > 1 && SelectionService.HierarchyContent.Any(item => item.FolderPath != null);
    private unsafe void PublishHierarchyContentDrag()
    {
        if (activeHierarchyContentDrag == null || activeHierarchyContentDrag.SelectionVersion != SelectionService.ContentVersion
            || activeHierarchyContentDrag.Owners.Any(owner => !IsCurrentHierarchyDragSource(owner.Document)))
        {
            var items = CommonHierarchyDragContent();
            activeHierarchyContentDrag = new(Guid.NewGuid(), SelectionService.ContentVersion, items,
                items.Select(item => item.Document).Distinct().Select(owner => new ContentDragOwner(owner, owner.StructureVersion, owner.FolderVersion, owner.SelectionVersion)).ToArray());
        }
        var payload = new HierarchyContentDragPayload { Token = activeHierarchyContentDrag.Token };
        Gui.SetDragDropPayload(HierarchyContentDragType, &payload, (nuint)sizeof(HierarchyContentDragPayload));
        foreach (var item in activeHierarchyContentDrag.Items)
            Gui.TextUnformatted(item.EntityId is { } id ? item.Document.Asset.Hierarchy.Parts[id].Entity.Name : item.FolderPath![(item.FolderPath.LastIndexOf('/') + 1)..]);
    }
    private HierarchyContentItem[] CommonHierarchyDragContent()
    {
        var selected = SelectionService.HierarchyContent.ToArray();
        var folders = selected.Where(item => item.FolderPath != null).Where(item => !selected.Any(other =>
            other.FolderPath != null && ReferenceEquals(other.Document, item.Document) && other.FolderOwner == item.FolderOwner
            && item.FolderPath!.StartsWith(other.FolderPath + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        var result = new List<HierarchyContentItem>();
        foreach (var owner in selected.Select(item => item.Document).Distinct())
        {
            var roots = owner.GetSelectedTransformRoots();
            foreach (var folder in folders.Where(folder => ReferenceEquals(folder.Document, owner)))
            {
                var parent = folder.FolderOwner is { } id && owner.Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.Entity : null;
                var included = false;
                for (var ancestor = parent; ancestor != null; ancestor = ancestor.Transform.Parent?.Entity)
                    if (roots.Contains(ancestor)) { included = true; break; }
                if (!included) result.Add(folder);
            }
            foreach (var entity in roots)
                if (!folders.Any(folder => ReferenceEquals(folder.Document, owner) && folder.FolderOwner == entity.Transform.Parent?.Entity.Id
                    && (string.Equals(owner.Asset.Hierarchy.Parts[entity.Id].Folder, folder.FolderPath, StringComparison.OrdinalIgnoreCase)
                        || (owner.Asset.Hierarchy.Parts[entity.Id].Folder ?? "").StartsWith(folder.FolderPath + "/", StringComparison.OrdinalIgnoreCase))))
                    result.Add(HierarchyContentItem.Entity(owner, entity.Id));
        }
        return result.ToArray();
    }
    private bool CanApplyHierarchyContentDrop(ContentDrop drop)
        => CanApplyHierarchyContentDrop(drop, out _);
    private bool CanApplyHierarchyContentDrop(ContentDrop drop, out bool noOp)
    {
        noOp = false;
        if (IsAuthoringPipelineUpdating || drop.Source.Items.Length == 0
            || drop.Source.Owners.All(owner => IsCurrentHierarchyContextOwner(owner.Document)) && SelectionService.ContentVersion != drop.Source.SelectionVersion
            || !IsCurrentHierarchyContextOwner(drop.Destination)
            || sessionHistory == null || !ReferenceEquals(drop.Destination.SessionHistory, sessionHistory)
            || !ReferenceEquals(authoring?.Find(drop.Destination.Asset.Id)?.Document, drop.Destination)
            || !drop.Destination.CanEditHierarchyFolders(drop.Parent)) return false;
        foreach (var owner in drop.Source.Owners)
            if (!IsCurrentHierarchyDragSource(owner.Document)
                || !ReferenceEquals(owner.Document.SessionHistory, sessionHistory) || owner.Document.StructureVersion != owner.Structure
                || owner.Document.FolderVersion != owner.Folder || owner.Document.SelectionVersion != owner.Selection) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasMove = false;
        foreach (var item in drop.Source.Items)
        {
            if (item.FolderPath is { } path)
            {
                var name = path[(path.LastIndexOf('/') + 1)..];
                if (!paths.Add(name)) return false;
                if (ReferenceEquals(item.Document, drop.Destination))
                {
                    if (!item.Document.CanMoveHierarchyFolder(item.FolderOwner, path, drop.Parent, drop.Folder, allowSameLocation: true)) return false;
                    var replacement = drop.Folder.Length == 0 ? name : drop.Folder + "/" + name;
                    hasMove |= item.FolderOwner != drop.Parent || !string.Equals(path, replacement, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    if (!item.Document.CanMoveHierarchyFolderToScene(drop.Destination, item.FolderOwner, path, drop.Parent, drop.Folder)) return false;
                    hasMove = true;
                }
            }
            else if (item.EntityId is { } id && item.Document.Asset.Hierarchy.Parts.TryGetValue(id, out var part))
            {
                if (ReferenceEquals(item.Document, drop.Destination))
                {
                    if (!drop.Destination.CanMoveEntitiesToFolder([part.Entity], drop.Parent)) return false;
                    var parent = part.Entity.Transform.Parent;
                    var index = parent?.Children.IndexOf(part.Entity.Transform) ?? drop.Destination.Asset.Hierarchy.RootParts.IndexOf(part.Entity);
                    hasMove |= drop.Insertion || parent?.Entity.Id != drop.Parent || !string.Equals(part.Folder ?? "", drop.Folder, StringComparison.OrdinalIgnoreCase)
                        || index != drop.Index - 1;
                }
                else if (!item.Document.CanMoveHierarchyRootsTo(drop.Destination, [part.Entity])
                    || item.Document.HasUnloadableObjects) return false;
                else hasMove = true;
            }
            else return false;
        }
        var folderExists = drop.Folder.Length == 0 || drop.Destination.GetHierarchyFolders(drop.Parent).Contains(drop.Folder, StringComparer.OrdinalIgnoreCase);
        noOp = !hasMove && folderExists;
        return hasMove && folderExists;
    }
    private unsafe void AcceptHierarchyContentDrop(EntityHierarchyDocument destination, Guid? parent, string folder, int index,
        bool? insertAfter = null, float? insertIndent = null, HierarchyEntityDropTarget? fallback = null, Entity? insertItem = null)
    {
        var payload = Gui.AcceptDragDropPayload(HierarchyContentDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (payload.IsNull || payload.DataSize != sizeof(HierarchyContentDragPayload) || activeHierarchyContentDrag is not { } source
            || ((HierarchyContentDragPayload*)payload.Data)->Token != source.Token) return;
        var drop = new ContentDrop(source, destination, parent, folder, index, Gui.GetIO().KeyAlt, insertAfter != null);
        var accepted = CanApplyHierarchyContentDrop(drop, out var noOp);
        if (accepted && insertAfter != null && insertItem != null
            && source.Items.Any(item => ReferenceEquals(item.Document, destination) && item.EntityId == insertItem.Id)) return;
        if (!accepted && !noOp && insertAfter != null && fallback is { } inside)
        {
            drop = new ContentDrop(source, destination, inside.Parent?.Id, inside.Folder, inside.Index, Gui.GetIO().KeyAlt);
            accepted = CanApplyHierarchyContentDrop(drop, out _);
            insertAfter = null; insertIndent = null;
        }
        if (!accepted) return;
        DrawHierarchyInsertionCue(insertAfter, insertIndent);
        if (payload.IsDelivery()) deliveredHierarchyContentDrop = drop;
    }
    private void ApplyHierarchyContentDrop()
    {
        if (deliveredHierarchyContentDrop is not { } drop) return;
        deliveredHierarchyContentDrop = null;
        Execute(() =>
        {
            if (!CanApplyHierarchyContentDrop(drop)) throw new InvalidOperationException("The selected hierarchy content changed.");
            ValidatePendingPropertyStrings();
            documents.CommitAll(); FinishEntityRename(true); engineViewport?.Picker?.Cancel("Hierarchy drop");
            if (!CanApplyHierarchyContentDrop(drop)) throw new InvalidOperationException("The selected hierarchy content changed.");
            var selectionFixup = new HierarchyMoveSelectionPlan(this);
            var index = drop.Index;
            var applied = false;
            ApplyCompositeHierarchyCommand(drop.Source.Owners.Select(owner => owner.Document).ToArray(), EntityHierarchyDocument.HierarchyTransferOperationName, _ =>
            {
                if (applied) return;
                applied = true;
                // Native MoveChildren collects direct entities first, followed by the entities within dragged folders.
                foreach (var item in drop.Source.Items.OrderBy(item => item.FolderPath == null ? 0 : 1))
                {
                    var owner = item.Document;
                    IReadOnlyList<Entity> moved;
                    if (item.FolderPath is { } path)
                    {
                        if (ReferenceEquals(owner, drop.Destination)) moved = owner.MoveHierarchyFolder(item.FolderOwner, path, drop.Parent, drop.Folder, drop.KeepWorld, index, allowSameLocation: true);
                        else moved = owner.MoveHierarchyFolderToScene(drop.Destination, item.FolderOwner, path, drop.Parent, drop.Folder, drop.KeepWorld,
                            GetSceneHierarchyOffset(owner), GetSceneHierarchyOffset(drop.Destination), index);
                        var name = path[(path.LastIndexOf('/') + 1)..];
                        selectionFixup.Folder(owner, item.FolderOwner, path, drop.Destination, drop.Parent,
                            drop.Folder.Length == 0 ? name : drop.Folder + "/" + name);
                    }
                    else
                    {
                        var entity = owner.Asset.Hierarchy.Parts[item.EntityId!.Value].Entity;
                        if (ReferenceEquals(owner, drop.Destination))
                        { owner.MoveEntitiesToFolder([entity], drop.Parent, drop.Folder, drop.KeepWorld, index); moved = [entity]; }
                        else
                        {
                            var prepared = owner.PrepareMoveToScene(drop.Destination, drop.Parent, drop.Folder, drop.KeepWorld,
                                GetSceneHierarchyOffset(owner), GetSceneHierarchyOffset(drop.Destination), index, [entity]);
                            owner.ApplyPreparedSceneMove(prepared); moved = prepared.MovedRoots;
                        }
                        selectionFixup.Entity(owner, item.EntityId.Value, drop.Destination, moved[0].Id);
                    }
                    if (moved.Count > 0)
                    {
                        var last = moved[^1];
                        index = (drop.Parent is { } parent ? drop.Destination.Asset.Hierarchy.Parts[parent].Entity.Transform.Children.IndexOf(last.Transform)
                            : drop.Destination.Asset.Hierarchy.RootParts.IndexOf(last)) + 1;
                    }
                }
            }, allowRetainedDragSources: true, selectionFixup: selectionFixup);
            var primary = SelectionService.PrimaryHierarchyContent;
            folderSelectionDocument = primary?.Document;
            selectedHierarchyFolder = primary is { FolderPath: { } folderPath } folderItem ? (folderItem.FolderOwner, folderPath) : null;
            RevealHierarchyContext(drop.Destination, drop.Destination.Selected?.Id, drop.Parent, selectedHierarchyFolder?.Path ?? drop.Folder);
        });
    }
}
