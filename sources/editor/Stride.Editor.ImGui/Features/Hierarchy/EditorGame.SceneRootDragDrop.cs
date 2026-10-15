using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string SceneRootDragType = "STRIDE_SCENE_ROOT";
    private struct SceneRootDragPayload { internal AssetId Scene; internal Guid Generation; internal Guid Selection; }
    private sealed record SceneRootDragSelection(Guid Token, long Version, SceneDocument[] Scenes);
    private SceneRootDragSelection? sceneRootDragSelection;
    private SceneDocument? sceneRootPointerPress;
    private bool sceneRootPointerDragged;
    private void ClearSceneRootDragState()
    {
        sceneRootDragSelection = null; sceneRootPointerPress = null; sceneRootPointerDragged = false;
    }

    private void QueueSceneRootPointerSelection(SceneDocument scene)
    {
        var io = Gui.GetIO();
        if (Gui.IsMouseDragging(ImGuiMouseButton.Left)) sceneRootPointerDragged = true;
        if (Gui.IsItemClicked(ImGuiMouseButton.Left) && !Gui.IsItemToggledOpen() && io.MousePos.X < hierarchyRowMaximum.X - HierarchyActionWidth)
        {
            sceneRootPointerPress = scene; sceneRootPointerDragged = false;
            if (!SelectionService.IsSceneRootSelected(scene) || SelectionService.SelectedSceneRoots.Count <= 1 || io.KeyCtrl || io.KeyShift)
            {
                SelectionService.QueueSceneRootClick(scene, io.KeyCtrl, io.KeyShift);
                sceneRootPointerPress = null;
            }
        }
        if (ReferenceEquals(sceneRootPointerPress, scene) && Gui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!sceneRootPointerDragged && Gui.IsItemHovered()) SelectionService.QueueSceneRootClick(scene, false, false);
            sceneRootPointerPress = null;
        }
    }

    private bool IsCurrentSceneRoot(SceneDocument scene) => sessionHistory != null && scene.CanSave
        && ReferenceEquals(scene.SessionHistory, sessionHistory)
        && IsCurrentHierarchyContextOwner(scene);

    private bool CanMoveSceneRoot(SceneDocument source, SceneDocument destination, int index, out string reason)
    {
        reason = "Can only add a scene to another scene";
        if (IsAuthoringPipelineUpdating || !IsCurrentSceneRoot(source) || !IsCurrentSceneRoot(destination)
            || index < 0 || index > destination.Asset.ChildrenIds.Count) return false;
        if (ReferenceEquals(source, destination)) { reason = "Can't be a parent of itself"; return false; }
        if (SceneParentId(source.Asset) is not { } previous || ResolveSceneHierarchyDocument(previous) is not { } old
            || !IsCurrentSceneRoot(old) || !old.Asset.ChildrenIds.Contains(source.Asset.Id)) return false;
        var visited = new HashSet<AssetId>();
        SceneDocument? ancestor = destination;
        while (ancestor != null)
        {
            if (!visited.Add(ancestor.Asset.Id) || ancestor.Asset.Id == source.Asset.Id)
            { reason = "Circular scene hierarchy"; return false; }
            if (SceneParentId(ancestor.Asset) is not { } parent) break;
            ancestor = ResolveSceneHierarchyDocument(parent);
            if (ancestor == null) return false;
        }
        reason = $"Add as a child to {System.IO.Path.GetFileNameWithoutExtension(destination.Path)}";
        return true;
    }

    private SceneDocument[] CommonSceneRootSelection(SceneDocument[] scenes)
    {
        var chosen = scenes.Select(scene => scene.Asset.Id).ToHashSet();
        return scenes.Where(scene =>
        {
            var visited = new HashSet<AssetId>(); var parent = SceneParentId(scene.Asset);
            while (parent is { } id)
            {
                if (!visited.Add(id)) throw new InvalidOperationException("Circular scene hierarchy");
                if (chosen.Contains(id)) return false;
                var owner = ResolveSceneHierarchyDocument(id) ?? throw new InvalidOperationException("The parent scene is not available.");
                parent = SceneParentId(owner.Asset);
            }
            return true;
        }).ToArray();
    }

    private bool CanMoveSceneRoots(SceneDocument[] sources, SceneDocument destination, int index, bool sameParent, out string reason)
    {
        reason = "Can only add a scene to another scene";
        if (sources.Length == 0 || SelectionService.HierarchyContent.Count > 0) return false;
        foreach (var source in sources)
        {
            if (!CanMoveSceneRoot(source, destination, index, out reason)) return false;
            if (!sameParent && SceneParentId(source.Asset) == destination.Asset.Id) { reason = "Scene already has this parent"; return false; }
        }
        return true;
    }

    private void MoveSceneRoots(SceneRootDragSelection selection, SceneDocument destination, int index, bool sameParent)
    {
        if (selection.Version != SelectionService.ContentVersion || !selection.Scenes.SequenceEqual(SelectionService.SelectedSceneRoots)
            || !selection.Scenes.All(IsCurrentSceneRoot))
            throw new InvalidOperationException("The selected scenes changed.");
        var sources = CommonSceneRootSelection(selection.Scenes);
        if (!CanMoveSceneRoots(sources, destination, index, sameParent, out var reason)) throw new InvalidOperationException(reason);
        var changed = new Dictionary<AssetId, SceneHierarchyPlacement>();
        var baseline = new Dictionary<AssetId, (SceneDocument Owner, AssetId[] Children, AssetId? Parent)>();
        void Add(SceneDocument owner)
        {
            changed.TryAdd(owner.Asset.Id, new(owner, owner.Asset.ChildrenIds.ToArray(), owner.Asset.Parent, owner.Asset.Offset));
            baseline.TryAdd(owner.Asset.Id, (owner, owner.Asset.ChildrenIds.ToArray(), SceneParentId(owner.Asset)));
        }
        Add(destination);
        var parentReference = (Scene)ContentAssetReference.Create(typeof(Scene), workspace!.FindAsset(destination.Asset.Id));
        foreach (var source in sources)
        {
            var old = ResolveSceneHierarchyDocument(SceneParentId(source.Asset)!.Value)!; Add(old); Add(source);
            var children = changed[old.Asset.Id].Children.ToList(); var sourceIndex = children.IndexOf(source.Asset.Id);
            if (ReferenceEquals(old, destination) && sourceIndex < index) --index;
            children.RemoveAt(sourceIndex); changed[old.Asset.Id] = changed[old.Asset.Id] with { Children = children.ToArray() };
            if (!ReferenceEquals(old, destination)) changed[source.Asset.Id] = changed[source.Asset.Id] with { Parent = parentReference };
        }
        var targetChildren = changed[destination.Asset.Id].Children.ToList(); targetChildren.InsertRange(index, sources.Select(source => source.Asset.Id));
        changed[destination.Asset.Id] = changed[destination.Asset.Id] with { Children = targetChildren.ToArray() };
        var mutations = changed.Values.Where(state => !state.Children.SequenceEqual(state.Document.Asset.ChildrenIds)
            || !Equals(SceneParentId(state.Document.Asset), state.Parent == null ? null : Stride.Core.Serialization.AttachedReferenceManager.GetAttachedReference(state.Parent)?.Id)).ToArray();
        if (mutations.Length == 0) return;
        ValidatePendingPropertyStrings();
        documents.CommitAll();
        if (selection.Version != SelectionService.ContentVersion || !selection.Scenes.SequenceEqual(SelectionService.SelectedSceneRoots)
            || baseline.Values.Any(state => !IsCurrentSceneRoot(state.Owner) || !state.Owner.Asset.ChildrenIds.SequenceEqual(state.Children)
                || SceneParentId(state.Owner.Asset) != state.Parent))
            throw new InvalidOperationException("The scene hierarchy changed before the drop.");
        // Parent/order edits preserve the freshly committed native Offset, including a validated property draft.
        mutations = mutations.Select(state => state with { Offset = state.Document.Asset.Offset }).ToArray();
        var participants = changed.Values.Select(state => authoring!.Find(state.Document.Asset.Id)!).ToArray();
        using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            foreach (var state in mutations) state.Document.ApplySceneHierarchy(state.Children, state.Parent, state.Offset, participants);
        });
        capture.Publish(sessionHistory, sources.Length == 1 ? "Move child scene" : "Move child scenes");
    }

    private unsafe void DrawSceneRootDragDrop(SceneDocument scene, SceneDocument presentedRoot, bool emptyArea = false)
    {
        if (!Gui.IsMouseDown(ImGuiMouseButton.Left) && !Gui.IsMouseReleased(ImGuiMouseButton.Left))
            ClearSceneRootDragState();
        var minimum = hierarchyRowMinimum; var maximum = hierarchyRowMaximum;
        if (!emptyArea && !ReferenceEquals(scene, presentedRoot) && IsCurrentSceneRoot(scene) && !IsAuthoringPipelineUpdating && Gui.BeginDragDropSource())
        {
            sceneRootPointerDragged = true;
            if (!SelectionService.IsSceneRootSelected(scene))
            { SelectionService.QueueSceneRootClick(scene, false, false); CompleteHierarchyContentSelection(); }
            if (sceneRootDragSelection == null || sceneRootDragSelection.Version != SelectionService.ContentVersion)
                sceneRootDragSelection = new(Guid.NewGuid(), SelectionService.ContentVersion, SelectionService.SelectedSceneRoots.ToArray());
            var data = new SceneRootDragPayload { Scene = scene.Asset.Id, Generation = scene.DragIdentity, Selection = sceneRootDragSelection.Token };
            Gui.SetDragDropPayload(SceneRootDragType, &data, (nuint)sizeof(SceneRootDragPayload));
            Gui.TextUnformatted(System.IO.Path.GetFileNameWithoutExtension(scene.Path));
            Gui.EndDragDropSource();
        }
        if (!Gui.BeginDragDropTarget()) return;
        var payload = Gui.AcceptDragDropPayload(SceneRootDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (!payload.IsNull && payload.DataSize == sizeof(SceneRootDragPayload))
        {
            var data = *(SceneRootDragPayload*)payload.Data;
            var source = authoring?.Find(data.Scene)?.Document as SceneDocument;
            var selection = sceneRootDragSelection;
            var band = HierarchyInsertBand(scene, emptyArea, minimum, maximum);
            var after = band.After;
            var resolved = after && !emptyArea ? ResolveHierarchyAfterTarget(scene) as SceneDocument ?? scene : scene;
            var edge = !ReferenceEquals(resolved, presentedRoot) && band.Edge;
            var destination = edge && SceneParentId(resolved.Asset) is { } parent ? ResolveSceneHierarchyDocument(parent) : scene;
            var index = edge && destination != null ? destination.Asset.ChildrenIds.IndexOf(resolved.Asset.Id) + (after ? 1 : 0)
                : scene.Asset.ChildrenIds.Count;
            var reason = "Can only add a scene to another scene";
            SceneDocument[] roots = [];
            var valid = source != null && source.DragIdentity == data.Generation && destination != null
                && selection != null && selection.Token == data.Selection && selection.Version == SelectionService.ContentVersion
                && selection.Scenes.SequenceEqual(SelectionService.SelectedSceneRoots) && selection.Scenes.All(IsCurrentSceneRoot);
            if (valid)
            {
                try
                {
                    roots = CommonSceneRootSelection(selection!.Scenes);
                    valid = CanMoveSceneRoots(roots, destination!, index, edge, out reason);
                    var noOp = valid && edge && roots.Contains(resolved);
                    if (!valid && edge)
                    {
                        destination = scene; index = scene.Asset.ChildrenIds.Count; edge = false;
                        valid = CanMoveSceneRoots(roots, destination, index, false, out reason);
                    }
                    if (noOp) { valid = false; reason = "Nothing to move"; }
                }
                catch (InvalidOperationException error) { valid = false; reason = error.Message; }
            }
            if (valid)
            {
                var color = Gui.GetColorU32(new System.Numerics.Vector4(.38f, .74f, .94f, 1));
                if (edge)
                {
                    var y = after ? maximum.Y : minimum.Y;
                    var indent = HierarchyController.State.DropRows.TryGetValue(resolved, out var geometry) ? geometry.Indent : minimum.X;
                    Gui.GetWindowDrawList().AddLine(new(indent, y), new(maximum.X, y), color, 2);
                }
                else Gui.GetWindowDrawList().AddRect(minimum, maximum, color);
                if (payload.IsDelivery())
                {
                    var selected = selection!; var selectedDestination = destination!; var insertion = index; var sameParent = edge;
                    pendingHierarchyPrefabNavigation = () => MoveSceneRoots(selected, selectedDestination, insertion, sameParent);
                }
            }
            Gui.BeginTooltip(); Gui.TextUnformatted(reason); Gui.EndTooltip();
        }
        Gui.EndDragDropTarget();
    }
}
