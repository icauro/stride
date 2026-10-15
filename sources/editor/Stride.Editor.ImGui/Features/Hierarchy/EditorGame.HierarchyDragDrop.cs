using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string EntityDragType = "STRIDE_ENTITY";
    private struct EntityDragPayload { internal Guid Document; internal Guid Entity; }
    private sealed record DragOwnerSelection(EntityHierarchyDocument Document, EntitySelectionSnapshot Selection,
        long SelectionVersion, long StructureVersion, long FolderVersion);
    private sealed record HierarchyDrop(EntityHierarchyDocument Source, EntityHierarchyDocument Destination,
        EntitySelectionSnapshot Selection, Entity Entity, Entity? Parent, int Index, bool KeepWorld, Entity[]? Group = null, string Folder = "",
        DragOwnerSelection[]? Owners = null);
    private HierarchyDrop? deliveredHierarchyDrop;
    private Guid? dragHoverParent;
    private WeakReference<EntityHierarchyDocument>? dragHoverOwner;
    private float dragHoverSeconds;
    private bool dragHoveredThisFrame;
    internal void CaptureDragCheckpoint() => captureCheckpointSuffix = ".hierarchy-drag.png";
    internal bool HierarchyOrderMatches => document != null && engineViewport?.MatchesHierarchyOrder(document) == true;
    private unsafe void DrawEntityDragDrop(Entity? target, EntityHierarchyDocument? owner = null, bool emptyArea = false)
    {
        if (document == null) return;
        var destination = owner ?? document;
        if (IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(destination)) return;
        var min = Gui.GetItemRectMin(); var max = Gui.GetItemRectMax();
        if (target == null && !emptyArea && !HierarchyController.State.DropRows.ContainsKey(HierarchyDropRoot(destination)))
            RememberHierarchyDropRow(HierarchyDropRoot(destination), HierarchyController.RowOpen, min.X);
        var dropTarget = ResolveHierarchyEntityDrop(target, destination);
        if (target != null && destination.CanDeleteEntity(target) && CanBeginHierarchyRowDrag(HierarchyContentItem.Entity(destination, target.Id))
            && Gui.BeginDragDropSource())
        {
            if (!IsHierarchyEntityContentSelected(destination, target.Id)) SelectCompositeHierarchyEntity(destination, target, false);
            BeginHierarchyEntityDrag(destination, target.Id);
            if (HasMixedHierarchyDrag) PublishHierarchyContentDrag();
            else
            {
                var data = new EntityDragPayload { Document = destination.DragIdentity, Entity = target.Id };
                Gui.SetDragDropPayload(EntityDragType, &data, (nuint)sizeof(EntityDragPayload));
                Gui.TextUnformatted(target.Name); Gui.TextDisabled("Alt: keep world transform");
            }
            Gui.EndDragDropSource();
        }
        if (!Gui.BeginDragDropTarget()) return;
        var payload = Gui.AcceptDragDropPayload(EntityDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (!payload.IsNull && payload.DataSize == sizeof(EntityDragPayload))
        {
            var data = *(EntityDragPayload*)payload.Data;
            var source = data.Document == destination.DragIdentity ? destination
                : authoring?.PublishedStates.Select(state => state.Document).OfType<EntityHierarchyDocument>().FirstOrDefault(value => value.DragIdentity == data.Document);
            if (source != null && IsCurrentHierarchyDragSource(source) && source.Asset.Hierarchy.Parts.TryGetValue(data.Entity, out var design)
                && TryGetHierarchyEntityDrag(source, data.Entity, out var origin))
            {
                var group = source.IsSelected(design.Entity.Id) && source.SelectedCount > 1 ? source.GetSelectedTransformRoots() : null;
                var selectedOwners = (IReadOnlyList<EntityHierarchyDocument>)origin.Documents;
                bool CanApply(HierarchyEntityDropTarget candidate)
                {
                    var candidateParent = candidate.Parent; var candidateIndex = candidate.Index;
                    var changesFolder = (design.Folder ?? "") != candidate.Folder;
                    var candidateValid = !ReferenceEquals(source, destination)
                    ? source.CanMoveHierarchyRootsTo(destination, group ?? [design.Entity])
                        && ReferenceEquals(source.SessionHistory, destination.SessionHistory) && destination.CanCreateEntity(candidateParent)
                        && destination.CanEditHierarchyFolders(candidateParent?.Id)
                    : group != null ? destination.CanMoveEntitiesToFolder(group, candidateParent?.Id) : destination.CanMoveAtIndex(design.Entity, candidateParent, candidateIndex)
                        || changesFolder && destination.CanMoveEntitiesToFolder([design.Entity], candidateParent?.Id);
                    if (selectedOwners.Count > 1) candidateValid = selectedOwners.All(selectedOwner => IsCurrentHierarchyDragSource(selectedOwner) && (ReferenceEquals(selectedOwner, destination)
                    ? destination.CanMoveEntitiesToFolder(selectedOwner.GetSelectedTransformRoots(), candidateParent?.Id)
                    : selectedOwner.CanMoveHierarchyRootsTo(destination, selectedOwner.GetSelectedTransformRoots())
                        && ReferenceEquals(selectedOwner.SessionHistory, destination.SessionHistory) && destination.CanCreateEntity(candidateParent)
                        && destination.CanEditHierarchyFolders(candidateParent?.Id)));
                    return candidateValid;
                }
                var acceptedTarget = dropTarget;
                var valid = CanApply(acceptedTarget);
                var noOp = acceptedTarget.Edge && valid && acceptedTarget.InsertItem is { } insertionItem
                    && (ReferenceEquals(source, destination) && design.Entity == insertionItem
                        || selectedOwners.Any(selectedOwner => ReferenceEquals(selectedOwner, destination) && selectedOwner.IsSelected(insertionItem.Id)));
                if (!valid && acceptedTarget.Edge && ReferenceEquals(source, destination) && group == null && selectedOwners.Count <= 1
                    && design.Entity.Transform.Parent?.Entity == acceptedTarget.Parent && string.Equals(design.Folder ?? "", acceptedTarget.Folder, StringComparison.OrdinalIgnoreCase)
                    && destination.CanMoveEntitiesToFolder([design.Entity], acceptedTarget.Parent?.Id))
                {
                    var previous = design.Entity.Transform.Parent?.Children.IndexOf(design.Entity.Transform) ?? destination.Asset.Hierarchy.RootParts.IndexOf(design.Entity);
                    noOp = acceptedTarget.Index == previous || acceptedTarget.Index == previous + 1;
                }
                if (!valid && !noOp && acceptedTarget.Edge)
                { acceptedTarget = InsideHierarchyEntityDrop(target, destination); valid = CanApply(acceptedTarget); }
                if (noOp) valid = false;
                var edge = acceptedTarget.Edge; var parent = acceptedTarget.Parent; var index = acceptedTarget.Index; var destinationFolder = acceptedTarget.Folder;
                var message = noOp ? "Nothing to move" : valid ? edge ? $"Insert {(acceptedTarget.After ? "after" : "before")} {acceptedTarget.InsertItem!.Name}"
                    : $"Move {design.Entity.Name} to {parent?.Name ?? "scene root"}" : "Cannot move here: unchanged, cyclic or read-only hierarchy";
                if (valid)
                {
                    if (!edge && target != null && (target.Transform.Children.Count > 0 || destination.GetHierarchyFolders(target.Id).Count > 0))
                    {
                        dragHoveredThisFrame = true;
                        if (dragHoverParent != target.Id || dragHoverOwner?.TryGetTarget(out var hoverOwner) != true || !ReferenceEquals(hoverOwner, destination))
                        { dragHoverOwner = new(destination); dragHoverParent = target.Id; dragHoverSeconds = 0; }
                        dragHoverSeconds += Gui.GetIO().DeltaTime;
                        if (dragHoverSeconds >= 0.6f) RevealHierarchyContext(destination, null, target.Id, clearFilter: false);
                    }
                    else { dragHoverParent = null; dragHoverOwner = null; dragHoverSeconds = 0; }
                    var color = Gui.GetColorU32(new System.Numerics.Vector4(0.38f, 0.74f, 0.94f, 1));
                    if (edge) { var y = acceptedTarget.After ? max.Y : min.Y; Gui.GetWindowDrawList().AddLine(new NVector2(acceptedTarget.Indent, y), new NVector2(max.X, y), color, 2); }
                    else Gui.GetWindowDrawList().AddRect(min, max, color);
                    if (payload.IsDelivery()) deliveredHierarchyDrop = new HierarchyDrop(source, destination, source.SnapshotSelection(), design.Entity, parent, index, Gui.GetIO().KeyAlt, group,
                        destinationFolder, origin.Owners);
                }
                Gui.BeginTooltip(); Gui.TextUnformatted(message); Gui.TextDisabled(Gui.GetIO().KeyAlt ? "Keep world transform" : "Keep local transform · Alt: keep world"); Gui.EndTooltip();
            }
        }
        AcceptAssetEntityRowDrop(target, destination, emptyArea);
        var insideTarget = InsideHierarchyEntityDrop(target, destination);
        AcceptHierarchyFolderObjectDrop(dropTarget.Parent?.Id, dropTarget.Folder, destination, dropTarget.Index, dropTarget.Edge ? dropTarget.After : null, dropTarget.Indent, insideTarget);
        AcceptHierarchyContentDrop(destination, dropTarget.Parent?.Id, dropTarget.Folder, dropTarget.Index, dropTarget.Edge ? dropTarget.After : null, dropTarget.Indent, insideTarget, dropTarget.InsertItem);
        Gui.EndDragDropTarget();
    }
    private void ApplyHierarchyDrop()
    {
        if (!dragHoveredThisFrame || !Gui.IsMouseDown(ImGuiMouseButton.Left)) { dragHoverParent = null; dragHoverOwner = null; dragHoverSeconds = 0; }
        dragHoveredThisFrame = false;
        if (deliveredHierarchyDrop is not { } drop) return;
        deliveredHierarchyDrop = null;
        Execute(() =>
        {
            if (!IsCurrentHierarchyDragSource(drop.Source) || !IsCurrentHierarchyContextOwner(drop.Destination))
                throw new InvalidOperationException("The scene authored generation changed during the drag.");
            if (IsAuthoringPipelineUpdating) return;
            ValidatePendingPropertyStrings();
            FinishEntityRename(true);
            if (drop.Owners?.Any(owner => !IsCurrentHierarchyDragOwner(owner)) == true)
                throw new InvalidOperationException("The hierarchy drag selection changed.");
            if (!IsCurrentHierarchyDragSource(drop.Source) || !IsCurrentHierarchyContextOwner(drop.Destination)
                || !drop.Source.Asset.Hierarchy.Parts.TryGetValue(drop.Entity.Id, out var part) || !ReferenceEquals(part.Entity, drop.Entity)
                || drop.Parent != null && (!drop.Destination.Asset.Hierarchy.Parts.TryGetValue(drop.Parent.Id, out var parent) || !ReferenceEquals(parent.Entity, drop.Parent)))
                throw new InvalidOperationException("The hierarchy drop destination changed.");
            var selectionFixup = new HierarchyMoveSelectionPlan(this);
            if (drop.Owners is { Length: > 1 } selections)
            {
                foreach (var selection in selections)
                {
                    if (!IsCurrentHierarchyDragSource(selection.Document))
                        throw new InvalidOperationException("The selected scene authored generation changed during the drag.");
                    selection.Document.RestoreSelection(selection.Selection.Ids, selection.Selection.PrimaryId);
                }
                var insertion = drop.Index;
                var moved = new List<Guid>(); var ownerIndex = 0;
                ApplyCompositeHierarchyCommand(selections.Select(selection => selection.Document).ToArray(), EntityHierarchyDocument.HierarchyTransferOperationName, owner =>
                {
                    var snapshot = selections.First(selection => ReferenceEquals(selection.Document, owner)).Selection;
                    owner.RestoreSelection(snapshot.Ids, snapshot.PrimaryId);
                    var roots = owner.GetSelectedTransformRoots();
                    IReadOnlyList<Entity> inserted;
                    if (ReferenceEquals(owner, drop.Destination))
                    {
                        owner.MoveEntitiesToFolder(roots, drop.Parent?.Id, drop.Folder, drop.KeepWorld, insertion); inserted = roots;
                        foreach (var root in roots) selectionFixup.Entity(owner, root.Id, drop.Destination, root.Id);
                    }
                    else
                    {
                        var prepared = owner.PrepareMoveToScene(drop.Destination, drop.Parent?.Id, drop.Folder, drop.KeepWorld,
                            GetSceneHierarchyOffset(owner), GetSceneHierarchyOffset(drop.Destination), insertion);
                        owner.ApplyPreparedSceneMove(prepared);
                        inserted = prepared.MovedRoots;
                        foreach (var root in roots) selectionFixup.Entity(owner, root.Id, drop.Destination, prepared.IdentityRemapping.GetValueOrDefault(root.Id, root.Id));
                    }
                    moved.AddRange(inserted.Select(entity => entity.Id));
                    var last = inserted[^1];
                    insertion = (drop.Parent?.Transform.Children.IndexOf(last.Transform) ?? drop.Destination.Asset.Hierarchy.RootParts.IndexOf(last)) + 1;
                    if (++ownerIndex == selections.Length) drop.Destination.RecordImportedAssetSelection(moved);
                }, allowRetainedDragSources: true, selectionFixup: selectionFixup);
            }
            else
            {
                ApplyCompositeHierarchyCommand([drop.Source], EntityHierarchyDocument.HierarchyTransferOperationName, owner =>
                {
                    if (!ReferenceEquals(owner, drop.Destination))
                    {
                        owner.RestoreSelection(drop.Selection.Ids, drop.Selection.PrimaryId);
                        var prepared = owner.PrepareMoveToScene(drop.Destination, drop.Parent?.Id, drop.Folder, drop.KeepWorld,
                            GetSceneHierarchyOffset(owner), GetSceneHierarchyOffset(drop.Destination), drop.Index);
                        owner.ApplyPreparedSceneMove(prepared);
                        foreach (var root in drop.Group ?? [drop.Entity])
                            selectionFixup.Entity(owner, root.Id, drop.Destination, prepared.IdentityRemapping.GetValueOrDefault(root.Id, root.Id));
                    }
                    else
                    {
                        if (drop.Group != null || (owner.Asset.Hierarchy.Parts[drop.Entity.Id].Folder ?? "") != drop.Folder)
                            owner.MoveEntitiesToFolder(drop.Group ?? [drop.Entity], drop.Parent?.Id, drop.Folder, drop.KeepWorld, drop.Index);
                        else owner.MoveAtIndex(drop.Entity, drop.Parent, drop.Index, drop.KeepWorld);
                        foreach (var root in drop.Group ?? [drop.Entity]) selectionFixup.Entity(owner, root.Id, drop.Destination, root.Id);
                    }
                }, allowRetainedDragSources: true, selectionFixup: selectionFixup);
            }
            RevealHierarchyContext(drop.Destination, drop.Destination.Selected?.Id, drop.Parent?.Id, drop.Folder);
        });
        engineViewport?.Picker?.Cancel("Hierarchy drop"); assetCommandContext = false;
    }
}
