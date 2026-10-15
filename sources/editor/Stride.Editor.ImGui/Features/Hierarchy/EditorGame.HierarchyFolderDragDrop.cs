using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record HierarchyFolderDrop(EntityHierarchyDocument Document, Entity[] Entities, Guid? Owner, string Path, bool KeepWorld, int Index);
    private HierarchyFolderDrop? deliveredHierarchyFolderDrop;
    private (Guid? Owner, string Path)? revealHierarchyFolder;
    private (Guid? Owner, string Path)? hoveredHierarchyFolder;
    private WeakReference<EntityHierarchyDocument>? hierarchyFolderHoverOwner;
    private float hierarchyFolderHoverSeconds;
    private bool hierarchyFolderHoveredThisFrame;
    private const string HierarchyFolderDragType = "STRIDE_HIERARCHY_FOLDER";
    private struct HierarchyFolderDragPayload { internal Guid Document, Token; }
    private sealed record HierarchyFolderDrag(EntityHierarchyDocument Document, Guid? Owner, string Path, Guid Token);
    private sealed record HierarchyFolderObjectDrop(EntityHierarchyDocument Source, EntityHierarchyDocument Document, Guid? SourceOwner, string SourcePath, Guid? TargetOwner, string TargetPath, bool KeepWorld, int Index);
    private HierarchyFolderDrag? activeHierarchyFolderDrag;
    private HierarchyFolderObjectDrop? deliveredHierarchyFolderObjectDrop;

    private unsafe void DrawHierarchyFolderDragSource(Guid? owner, string path, EntityHierarchyDocument? hierarchy = null)
    {
        var document = hierarchy ?? this.document;
        if (document == null || IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(document)
            || !document.CanEditHierarchyFolders(owner) || !CanBeginHierarchyRowDrag(HierarchyContentItem.Folder(document, owner, path))
            || !Gui.BeginDragDropSource()) return;
        EnsureHierarchyFolderSelected(document, owner, path);
        if (HasMixedHierarchyDrag)
        {
            PublishHierarchyContentDrag(); Gui.EndDragDropSource(); return;
        }
        if (activeHierarchyFolderDrag is not { } drag || drag.Document != document || drag.Owner != owner || drag.Path != path)
            activeHierarchyFolderDrag = new(document, owner, path, Guid.NewGuid());
        var data = new HierarchyFolderDragPayload { Document = document.DragIdentity, Token = activeHierarchyFolderDrag.Token };
        Gui.SetDragDropPayload(HierarchyFolderDragType, &data, (nuint)sizeof(HierarchyFolderDragPayload));
        Gui.TextUnformatted(path[(path.LastIndexOf('/') + 1)..]);
        Gui.EndDragDropSource();
    }

    // Called inside the existing row's native drag-drop target scope.
    private unsafe void AcceptHierarchyFolderObjectDrop(Guid? owner, string path, EntityHierarchyDocument? hierarchy = null, int? insertionIndex = null,
        bool? insertAfter = null, float? insertIndent = null, HierarchyEntityDropTarget? fallback = null)
    {
        var document = hierarchy ?? this.document;
        if (document == null || IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(document)) return;
        var payload = Gui.AcceptDragDropPayload(HierarchyFolderDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (payload.IsNull || payload.DataSize != sizeof(HierarchyFolderDragPayload) || activeHierarchyFolderDrag is not { } source) return;
        var data = *(HierarchyFolderDragPayload*)payload.Data;
        if (!IsCurrentHierarchyDragSource(source.Document) || data.Document != source.Document.DragIdentity || data.Token != source.Token) return;
        bool CanApply(Guid? targetOwner, string targetPath) => ReferenceEquals(source.Document, document)
            ? document.CanMoveHierarchyFolder(source.Owner, source.Path, targetOwner, targetPath)
            : source.Document.CanMoveHierarchyFolderToScene(document, source.Owner, source.Path, targetOwner, targetPath);
        var accepted = CanApply(owner, path);
        var name = source.Path[(source.Path.LastIndexOf('/') + 1)..];
        var replacement = path.Length == 0 ? name : path + "/" + name;
        var noOp = ReferenceEquals(source.Document, document) && source.Owner == owner && string.Equals(source.Path, replacement, StringComparison.OrdinalIgnoreCase)
            && document.CanMoveHierarchyFolder(source.Owner, source.Path, owner, path, allowSameLocation: true);
        if (!accepted && !noOp && insertAfter != null && fallback is { } inside)
        {
            owner = inside.Parent?.Id; path = inside.Folder; insertionIndex = inside.Index;
            insertAfter = null; insertIndent = null;
            accepted = CanApply(owner, path);
        }
        if (!accepted) return;
        DrawHierarchyInsertionCue(insertAfter, insertIndent);
        hierarchyFolderHoveredThisFrame = true;
        if (hoveredHierarchyFolder is not { } hovered || hovered.Owner != owner || hovered.Path != path
            || hierarchyFolderHoverOwner?.TryGetTarget(out var hoverOwner) != true || !ReferenceEquals(hoverOwner, document))
        { hierarchyFolderHoverOwner = new(document); hoveredHierarchyFolder = (owner, path); hierarchyFolderHoverSeconds = 0; }
        hierarchyFolderHoverSeconds += Gui.GetIO().DeltaTime;
        if (hierarchyFolderHoverSeconds >= .6f) RevealHierarchyContext(document, null, owner, path, clearFilter: false);
        var index = insertionIndex ?? (owner is { } parent ? document.Asset.Hierarchy.Parts[parent].Entity.Transform.Children.Count : document.Asset.Hierarchy.RootParts.Count);
        if (payload.IsDelivery()) deliveredHierarchyFolderObjectDrop = new(source.Document, document, source.Owner, source.Path, owner, path, Gui.GetIO().KeyAlt, index);
    }

    private unsafe void DrawHierarchyFolderDragDrop(Guid? owner, string path, EntityHierarchyDocument? hierarchy = null)
    {
        var document = hierarchy ?? this.document;
        if (document == null || IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(document) || !Gui.BeginDragDropTarget()) return;
        var insideTarget = InsideHierarchyFolderDrop(document, owner, path);
        var assetInside = InsideHierarchyAssetDrop(document, insideTarget);
        var assetRow = HierarchyContentItem.Folder(document, owner, path);
        var resolved = ResolveHierarchyFolderDrop(document, owner, path);
        owner = resolved.Parent?.Id; path = resolved.Folder;
        var payload = Gui.AcceptDragDropPayload(EntityDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (!payload.IsNull && payload.DataSize == sizeof(EntityDragPayload))
        {
            var data = *(EntityDragPayload*)payload.Data;
            var globalSource = data.Document == document.DragIdentity ? document
                : authoring?.PublishedStates.Select(state => state.Document).OfType<EntityHierarchyDocument>().FirstOrDefault(candidate => candidate.DragIdentity == data.Document);
            var origin = globalSource != null && TryGetHierarchyEntityDrag(globalSource, data.Entity, out var captured) ? captured : null;
            bool CanApplyTo(HierarchyEntityDropTarget candidate) => origin != null && origin.Documents.All(selectedOwner =>
            {
                var roots = selectedOwner.GetSelectedTransformRoots();
                if (roots.Length == 0 && ReferenceEquals(selectedOwner, globalSource) && selectedOwner.Asset.Hierarchy.Parts.TryGetValue(data.Entity, out var draggedPart))
                    roots = [draggedPart.Entity];
                return IsCurrentHierarchyDragSource(selectedOwner)
                    && (ReferenceEquals(selectedOwner, document) ? document.CanMoveEntitiesToFolder(roots, candidate.Parent?.Id)
                        : selectedOwner.CanMoveHierarchyRootsTo(document, roots)
                            && ReferenceEquals(selectedOwner.SessionHistory, document.SessionHistory) && document.CanCreateEntity(candidate.Parent)
                            && document.CanEditHierarchyFolders(candidate.Parent?.Id));
            });
            var acceptedInsertion = CanApplyTo(resolved);
            var noOp = resolved.Edge && acceptedInsertion && resolved.InsertItem is { } insertItem && origin != null
                && (ReferenceEquals(globalSource, document) && data.Entity == insertItem.Id
                    || origin.Documents.Any(selectedOwner => ReferenceEquals(selectedOwner, document) && selectedOwner.IsSelected(insertItem.Id)));
            if (resolved.Edge && !acceptedInsertion && !noOp && CanApplyTo(insideTarget))
            { resolved = insideTarget; owner = resolved.Parent?.Id; path = resolved.Folder; }
            var selections = (IReadOnlyList<EntityHierarchyDocument>?)origin?.Documents ?? [];
            var global = globalSource?.IsSelected(data.Entity) == true && selections.Count > 1;
            if (!noOp && global && globalSource!.Asset.Hierarchy.Parts.TryGetValue(data.Entity, out var dragged))
            {
                var parent = owner is { } targetId ? document.Asset.Hierarchy.Parts[targetId].Entity : null;
                var valid = selections.All(selectedOwner => IsCurrentHierarchyDragSource(selectedOwner) && (ReferenceEquals(selectedOwner, document)
                    ? document.CanMoveEntitiesToFolder(selectedOwner.GetSelectedTransformRoots(), owner)
                    : selectedOwner.CanMoveHierarchyRootsTo(document, selectedOwner.GetSelectedTransformRoots())
                        && ReferenceEquals(selectedOwner.SessionHistory, document.SessionHistory) && document.CanEditHierarchyFolders(owner)));
                if (valid)
                {
                    if (payload.IsDelivery()) deliveredHierarchyDrop = new(globalSource, document, globalSource.SnapshotSelection(), dragged.Entity, parent,
                        resolved.Index, Gui.GetIO().KeyAlt, globalSource.GetSelectedTransformRoots(), path,
                        origin!.Owners);
                    DrawHierarchyInsertionCue(resolved.Edge ? resolved.After : null, resolved.Indent);
                }
            }
            if (!noOp && !global && origin != null && data.Document != document.DragIdentity)
            {
                var source = origin.Source;
                if (IsCurrentHierarchyDragSource(source) && source.CanMoveHierarchyRootsTo(document, source.GetSelectedTransformRoots()) && document.CanEditHierarchyFolders(owner)
                    && ReferenceEquals(source.SessionHistory, document.SessionHistory) && source.Asset.Hierarchy.Parts.TryGetValue(data.Entity, out var sourcePart))
                {
                    var parent = owner is { } parentId ? document.Asset.Hierarchy.Parts[parentId].Entity : null;
                    if (payload.IsDelivery()) deliveredHierarchyDrop = new(source, document, source.SnapshotSelection(), sourcePart.Entity, parent,
                        resolved.Index, Gui.GetIO().KeyAlt,
                        source.GetSelectedTransformRoots(), path, origin.Owners);
                    DrawHierarchyInsertionCue(resolved.Edge ? resolved.After : null, resolved.Indent);
                }
            }
            if (!noOp && !global && origin != null && data.Document == document.DragIdentity && document.Asset.Hierarchy.Parts.TryGetValue(data.Entity, out var part))
            {
                var entities = document.IsSelected(data.Entity) ? document.GetSelectedTransformRoots() : [part.Entity];
                if (document.CanMoveEntitiesToFolder(entities, owner))
                {
                    DrawHierarchyInsertionCue(resolved.Edge ? resolved.After : null, resolved.Indent);
                    hierarchyFolderHoveredThisFrame = true;
                    if (hoveredHierarchyFolder is not { } hovered || hovered.Owner != owner || hovered.Path != path
                        || hierarchyFolderHoverOwner?.TryGetTarget(out var hoverOwner) != true || !ReferenceEquals(hoverOwner, document))
                    { hierarchyFolderHoverOwner = new(document); hoveredHierarchyFolder = (owner, path); hierarchyFolderHoverSeconds = 0; }
                    hierarchyFolderHoverSeconds += Gui.GetIO().DeltaTime;
                    if (hierarchyFolderHoverSeconds >= .6f) RevealHierarchyContext(document, null, owner, path, clearFilter: false);
                    if (payload.IsDelivery()) deliveredHierarchyFolderDrop = new(document, entities, owner, path, Gui.GetIO().KeyAlt, resolved.Index);
                }
            }
        }
        AcceptHierarchyFolderObjectDrop(owner, path, document, resolved.Index, resolved.Edge ? resolved.After : null, resolved.Indent, insideTarget);
        AcceptHierarchyContentDrop(document, owner, path, resolved.Index, resolved.Edge ? resolved.After : null, resolved.Indent, insideTarget, resolved.InsertItem);
        var assetTarget = ResolveHierarchyAssetTarget(assetRow, assetInside);
        AcceptHierarchyAssetDrop(assetTarget.Parent, assetTarget.Folder, assetTarget.Index,
            assetTarget.Folder.Length > 0 ? assetTarget.Folder[(assetTarget.Folder.LastIndexOf('/') + 1)..] : "root level", destination: assetTarget.Document,
            rootAdd: assetTarget.RootAdd, insertAfter: assetTarget.After, insertIndent: assetTarget.Indent, fallback: assetInside, boundary: assetTarget.Boundary);
        Gui.EndDragDropTarget();
    }

    private void ApplyHierarchyFolderDrop()
    {
        ApplyHierarchyContentDrop();
        if (!hierarchyFolderHoveredThisFrame || !Gui.IsMouseDown(ImGuiMouseButton.Left))
        { hoveredHierarchyFolder = null; hierarchyFolderHoverOwner = null; hierarchyFolderHoverSeconds = 0; }
        hierarchyFolderHoveredThisFrame = false;
        if (deliveredHierarchyFolderObjectDrop is { } folderDrop)
        {
            deliveredHierarchyFolderObjectDrop = null;
            if (IsCurrentHierarchyContextOwner(folderDrop.Document) && IsCurrentHierarchyDragSource(folderDrop.Source))
                Execute(() =>
                {
                    if (IsAuthoringPipelineUpdating || (ReferenceEquals(folderDrop.Source, folderDrop.Document)
                        ? !folderDrop.Source.CanMoveHierarchyFolder(folderDrop.SourceOwner, folderDrop.SourcePath, folderDrop.TargetOwner, folderDrop.TargetPath)
                        : !folderDrop.Source.CanMoveHierarchyFolderToScene(folderDrop.Document, folderDrop.SourceOwner, folderDrop.SourcePath, folderDrop.TargetOwner, folderDrop.TargetPath))) return;
                    ValidatePendingPropertyStrings();
                    FinishEntityRename(true); engineViewport?.Picker?.Cancel("Hierarchy folder drop");
                    if (!IsCurrentHierarchyContextOwner(folderDrop.Document) || !IsCurrentHierarchyDragSource(folderDrop.Source))
                        throw new InvalidOperationException("The hierarchy drop owner changed.");
                    var selectionFixup = new HierarchyMoveSelectionPlan(this);
                    var name = folderDrop.SourcePath[(folderDrop.SourcePath.LastIndexOf('/') + 1)..];
                    var path = folderDrop.TargetPath.Length == 0 ? name : folderDrop.TargetPath + "/" + name;
                    selectionFixup.Folder(folderDrop.Source, folderDrop.SourceOwner, folderDrop.SourcePath, folderDrop.Document, folderDrop.TargetOwner, path);
                    ApplyCompositeHierarchyCommand([folderDrop.Source], EntityHierarchyDocument.HierarchyTransferOperationName, owner =>
                    {
                        if (ReferenceEquals(owner, folderDrop.Document)) owner.MoveHierarchyFolder(folderDrop.SourceOwner,
                            folderDrop.SourcePath, folderDrop.TargetOwner, folderDrop.TargetPath, folderDrop.KeepWorld, folderDrop.Index);
                        else owner.MoveHierarchyFolderToScene(folderDrop.Document, folderDrop.SourceOwner, folderDrop.SourcePath,
                            folderDrop.TargetOwner, folderDrop.TargetPath, folderDrop.KeepWorld,
                            GetSceneHierarchyOffset(owner), GetSceneHierarchyOffset(folderDrop.Document), folderDrop.Index);
                    }, allowRetainedDragSources: true, selectionFixup: selectionFixup);
                    var primary = SelectionService.PrimaryHierarchyContent;
                    folderSelectionDocument = primary?.Document;
                    selectedHierarchyFolder = primary is { FolderPath: { } folderPath } folderItem ? (folderItem.FolderOwner, folderPath) : null;
                    RevealHierarchyContext(folderDrop.Document, null, folderDrop.TargetOwner, path); assetCommandContext = false;
                });
        }
        if (!Gui.IsMouseDown(ImGuiMouseButton.Left)) { activeHierarchyFolderDrag = null; activeHierarchyContentDrag = null; activeHierarchyEntityDrag = null; }
        if (deliveredHierarchyFolderDrop is not { } drop) return;
        deliveredHierarchyFolderDrop = null;
        if (!IsCurrentHierarchyContextOwner(drop.Document)) return;
        Execute(() =>
        {
            FinishEntityRename(true); engineViewport?.Picker?.Cancel("Hierarchy folder drop");
            var selectionFixup = new HierarchyMoveSelectionPlan(this);
            foreach (var entity in drop.Entities) selectionFixup.Entity(drop.Document, entity.Id, drop.Document, entity.Id);
            ApplyCompositeHierarchyCommand([drop.Document], EntityHierarchyDocument.HierarchyTransferOperationName,
                owner => owner.MoveEntitiesToFolder(drop.Entities, drop.Owner, drop.Path, drop.KeepWorld, drop.Index), selectionFixup: selectionFixup);
            RevealHierarchyContext(drop.Document, drop.Document.Selected?.Id, drop.Owner, drop.Path);
            selectedHierarchyFolder = null; assetCommandContext = false;
        });
    }
}
