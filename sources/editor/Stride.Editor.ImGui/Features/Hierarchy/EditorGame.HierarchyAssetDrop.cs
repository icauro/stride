using Hexa.NET.ImGui;
using Stride.Assets.Entities;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record AssetEntityDrop(AssetId[] Assets, EntityHierarchyDocument Document, Guid? Parent, string Folder,
        int Index, bool CreateContainer, Vector3? WorldPosition, EngineViewport? Viewport, EntityHierarchyDocument? Presentation, bool RootAdd,
        object? Boundary, bool BoundaryAfter);
    private AssetEntityDrop? deliveredAssetDrop;

    // Invoked within the existing entity/folder/viewport native target scope.
    private unsafe void AcceptHierarchyAssetDrop(Guid? parentId, string folder, int index, string targetName,
        Vector3? worldPosition = null, EntityHierarchyDocument? destination = null, bool rootAdd = false, bool? insertAfter = null, float? insertIndent = null,
        HierarchyAssetDropTarget? fallback = null, object? boundary = null)
    {
        if (document == null || workspace == null) return;
        var payload = Gui.AcceptDragDropPayload(AssetDragType,
            ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (payload.IsNull || payload.DataSize != sizeof(AssetLocationPayload)) return;
        var source = *(AssetLocationPayload*)payload.Data;
        if (!TryGetAssetDragIds(source, out var assets)) return;
        var target = destination ?? document;
        var reason = "Can only add a scene to another scene";
        var valid = !rootAdd || TryGetRootAssetDropIndex(assets, target, out index);
        if (valid) valid = CanDropAssets(assets, target, parentId, folder, index, out reason);
        if (!valid && insertAfter != null && fallback is { } inside)
        {
            target = inside.Document; parentId = inside.Parent; folder = inside.Folder; index = inside.Index; rootAdd = inside.RootAdd;
            targetName = inside.Folder.Length > 0 ? inside.Folder[(inside.Folder.LastIndexOf('/') + 1)..]
                : inside.Parent is { } insideParent && target.Asset.Hierarchy.Parts.TryGetValue(insideParent, out var insidePart) ? insidePart.Entity.Name : "root level";
            insertAfter = null; insertIndent = null;
            boundary = null;
            valid = !rootAdd || TryGetRootAssetDropIndex(assets, target, out index);
            if (valid) valid = CanDropAssets(assets, target, parentId, folder, index, out reason);
        }
        var createContainer = !Gui.GetIO().KeyAlt;
        if (valid)
        {
            var min = Gui.GetItemRectMin(); var max = Gui.GetItemRectMax();
            var color = Gui.GetColorU32(new System.Numerics.Vector4(.38f, .74f, .94f, 1));
            if (insertAfter is { } after)
            {
                var y = after ? max.Y : min.Y;
                Gui.GetWindowDrawList().AddLine(new System.Numerics.Vector2(insertIndent ?? min.X, y), new(max.X, y), color, 2);
            }
            else Gui.GetWindowDrawList().AddRect(min, max, color);
            if (payload.IsDelivery()) deliveredAssetDrop = new(assets.ToArray(), target, parentId, folder, index,
                createContainer, worldPosition, worldPosition.HasValue ? engineViewport : null,
                worldPosition.HasValue ? engineViewport?.PresentedDocument : null, rootAdd, boundary, insertAfter == true);
        }
        Gui.BeginTooltip();
        var last = assets.Length == 0 ? null : workspace.AssetItems.FirstOrDefault(item => item.Id == assets[^1])?.Asset;
        var insertionName = boundary is SceneDocument scene ? Path.GetFileNameWithoutExtension(scene.Path)
            : boundary is HierarchyContentItem { EntityId: { } entityId } boundaryItem && boundaryItem.Document.Asset.Hierarchy.Parts.TryGetValue(entityId, out var design)
                ? design.Entity.Name : null;
        Gui.TextUnformatted(!valid ? reason : insertAfter is { } insertionAfter && insertionName != null ? $"Insert {(insertionAfter ? "after" : "before")} {insertionName}"
            : last is PrefabAsset ? createContainer
            ? $"Add the prefab to {targetName}\r\n(Hold Alt to add without a container entity)"
            : $"Add the prefab to {targetName}\r\n(Release Alt to add with a container entity)"
            : last is SceneAsset ? "Add to this scene"
            : last is Stride.Assets.Sprite.SpriteSheetAsset ? $"Add to {targetName}" : $"Add the selection into {targetName}");
        Gui.EndTooltip();
    }
    private void AcceptAssetEntityRowDrop(Entity? target, EntityHierarchyDocument? destination = null, bool emptyArea = false)
    {
        var owner = destination ?? document;
        if (owner == null || !IsCurrentHierarchyContextOwner(owner)) return;
        var inside = InsideHierarchyAssetDrop(owner, InsideHierarchyEntityDrop(target, owner), target == null && owner is SceneDocument);
        var row = target == null ? HierarchyDropRoot(owner) : (object)HierarchyContentItem.Entity(owner, target.Id);
        var resolved = ResolveHierarchyAssetTarget(row, inside, emptyArea);
        AcceptHierarchyAssetDrop(resolved.Parent, resolved.Folder, resolved.Index, "root level", destination: resolved.Document,
            rootAdd: resolved.RootAdd, insertAfter: resolved.After, insertIndent: resolved.Indent, fallback: inside, boundary: resolved.Boundary);
    }
    private void DrawSceneAssetDropTarget()
    {
        if (document == null || projectContent == null || engineViewport?.PresentedDocument == null || !Gui.BeginDragDropTarget()) return;
        var size = SceneImageSize;
        if (size.X > 0 && size.Y > 0)
        {
            var mouse = Gui.GetIO().MousePos;
            var normalized = new Vector2((mouse.X - SceneImageMinimum.X) / size.X, (mouse.Y - SceneImageMinimum.Y) / size.Y);
            var worldPosition = ScenePointAt(normalized);
            if (engineViewport.Gizmos is { TranslationSnapActive: true } gizmos) worldPosition = MathUtil.Snap(worldPosition, gizmos.TranslationSnap);
            AcceptHierarchyAssetDrop(null, "", document.Asset.Hierarchy.RootParts.Count, "root level", worldPosition,
                rootAdd: true);
        }
        Gui.EndDragDropTarget();
    }
    private Vector3 ScenePointAt(Vector2 normalized)
    {
        if (projectContent == null || engineViewport?.PresentedDocument == null
            || !float.IsFinite(normalized.X) || !float.IsFinite(normalized.Y)) return Vector3.Zero;
        var camera = engineViewport!.Camera.Component;
        camera.Update();
        var ray = EditorGameHelper.CalculateRayFromMousePosition(camera, normalized, Matrix.Invert(camera.ViewMatrix));
        var plane = new Plane(Vector3.Zero, Vector3.UnitY);
        var dot = Math.Clamp(Vector3.Dot(ray.Direction, plane.Normal), -1, 1);
        var sign = Math.Sign(Vector3.Dot(ray.Position, plane.Normal) + plane.D);
        if (sign * (MathF.Acos(dot) - MathUtil.PiOverTwo) < 7.5f * MathUtil.Pi / 180
            || !plane.Intersects(ref ray, out Vector3 worldPosition))
            worldPosition = ray.Position + 20 * ray.Direction;
        return worldPosition;
    }
    private void ApplyDeliveredAssetDrop()
    {
        if (deliveredAssetDrop is not { } drop) return;
        deliveredAssetDrop = null;
        if (!IsCurrentHierarchyContextOwner(drop.Document)) return;
        Execute(() =>
        {
            void CheckDropScope()
            {
                if (drop.Boundary != null && (!TryHierarchyAssetBoundary(drop.Boundary, drop.BoundaryAfter, out var current)
                    || !ReferenceEquals(current.Document, drop.Document) || current.Parent != drop.Parent || current.Folder != drop.Folder || current.Index != drop.Index))
                    throw new InvalidOperationException("The hierarchy asset insertion boundary changed.");
                if (drop.RootAdd && (!TryGetRootAssetDropIndex(drop.Assets, drop.Document, out var currentIndex) || currentIndex != drop.Index))
                    throw new InvalidOperationException("The scene root drop index changed.");
                if (drop.WorldPosition.HasValue && (drop.Viewport == null || drop.Presentation == null || projectContent == null
                    || !ReferenceEquals(engineViewport, drop.Viewport) || !ReferenceEquals(document, drop.Document)
                    || !ReferenceEquals(drop.Viewport.PresentedDocument, drop.Presentation)))
                    throw new InvalidOperationException("The scene drop viewport changed.");
            }
            CheckDropScope();
            if (!CanDropAssets(drop.Assets, drop.Document, drop.Parent, drop.Folder, drop.Index, out var reason)) throw new InvalidOperationException(reason);
            ValidatePendingPropertyStrings();
            FinishEntityRename(true); assetDocument?.EndEdit();
            CheckDropScope();
            engineViewport?.Picker?.Cancel("Asset drop");
            DropAssetsToHierarchy(drop.Assets, drop.Document, drop.Parent, drop.Folder, drop.Index, drop.CreateContainer, drop.WorldPosition,
                viewportDrop: drop.Viewport != null);
            assetCommandContext = false; hierarchyFilter = "";
            selectedHierarchyFolder = null;
            if (drop.Document.Selected is { } selected) compositeHierarchyPrimary = (drop.Document, selected.Id);
            RevealHierarchyContext(drop.Document, drop.Document.Selected?.Id, drop.Parent, drop.Folder);
        });
    }
}
