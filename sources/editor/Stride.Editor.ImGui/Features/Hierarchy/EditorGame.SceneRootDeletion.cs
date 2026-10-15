using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool HasOnlySceneRootSelection => SelectionService.SelectedSceneRoots.Count > 0
        && SelectionService.HierarchyContent.Count == 0;
    private bool ContainsPresentedSceneRoot => SelectionService.SelectedSceneRoots.Any(scene => ReferenceEquals(scene, engineViewport?.PresentedDocument));
    private bool CanRemoveSceneRoots(SceneDocument[] selected)
    {
        if (selected.Length == 0 || IsAuthoringPipelineUpdating || selected.Any(scene => ReferenceEquals(scene, engineViewport?.PresentedDocument))
            || selected.Any(scene => !IsCurrentSceneRoot(scene))) return false;
        try
        {
            foreach (var source in CommonSceneRootSelection(selected))
                if (!IsCurrentSceneRoot(source) || SceneParentId(source.Asset) is not { } parent
                    || ResolveSceneHierarchyDocument(parent) is not { } owner || !IsCurrentSceneRoot(owner)
                    || !owner.Asset.ChildrenIds.Contains(source.Asset.Id)) return false;
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private void RequestSelectedChildSceneRemoval()
    {
        var selected = SelectionService.SelectedSceneRoots.ToArray();
        var version = SelectionService.ContentVersion;
        pendingHierarchyPrefabNavigation = () => RemoveSelectedChildScenes(selected, version);
    }
    private void RemoveSelectedChildScenes(SceneDocument[] selected, long version)
    {
        if (!HasOnlySceneRootSelection || version != SelectionService.ContentVersion
            || !selected.SequenceEqual(SelectionService.SelectedSceneRoots) || !CanRemoveSceneRoots(selected))
            throw new InvalidOperationException("The selected scene hierarchy changed.");
        ValidatePendingPropertyStrings();
        documents.CommitAll();
        var roots = CommonSceneRootSelection(selected);
        var changed = new Dictionary<AssetId, SceneHierarchyPlacement>();
        foreach (var source in roots)
        {
            var parent = ResolveSceneHierarchyDocument(SceneParentId(source.Asset)!.Value)!;
            changed.TryAdd(parent.Asset.Id, new(parent, parent.Asset.ChildrenIds.ToArray(), parent.Asset.Parent, parent.Asset.Offset));
            var state = changed[parent.Asset.Id];
            changed[parent.Asset.Id] = state with { Children = state.Children.Where(id => id != source.Asset.Id).ToArray() };
            changed.Add(source.Asset.Id, new(source, source.Asset.ChildrenIds.ToArray(), null, source.Asset.Offset));
        }
        var participants = changed.Values.Select(state => authoring!.Find(state.Document.Asset.Id)!).ToArray();
        using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            foreach (var state in changed.Values) state.Document.ApplySceneHierarchy(state.Children, state.Parent, state.Offset, participants);
        });
        capture.Publish(sessionHistory, "Remove selected child scenes");
        ClearSceneRootDragState();
        ClearPresentedHierarchySelections();
        selectedHierarchyFolder = null; folderSelectionDocument = null;
        engineViewport?.Picker?.Cancel("Scene hierarchy changed");
    }
}
