using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorSelectionService
{
    private readonly List<SceneDocument> selectedSceneRoots = [];
    private readonly List<object> hierarchyVisibleRows = [];
    private object? hierarchyRangeAnchor;
    private SceneDocument? primarySceneRoot;
    private (SceneDocument Scene, bool Toggle, bool Range)? sceneRootClick;
    internal IReadOnlyList<SceneDocument> SelectedSceneRoots => selectedSceneRoots;
    internal AssetId? PrimarySceneRoot => primarySceneRoot?.Asset.Id;
    internal SceneDocument? PrimarySceneRootDocument => primarySceneRoot;
    internal bool HasPendingSceneRootClick => sceneRootClick != null;
    internal bool IsSceneRootSelected(SceneDocument scene) => selectedSceneRoots.Contains(scene);
    private bool IsAvailableSceneRoot(SceneDocument scene) => context.IsCurrentHierarchy(scene) && context.IsSceneRootAvailable(scene);

    private void BeginSceneRootRows()
    {
        hierarchyVisibleRows.Clear(); sceneRootClick = null;
        if (selectedSceneRoots.RemoveAll(scene => !IsAvailableSceneRoot(scene)) > 0) ContentVersion++;
        if (primarySceneRoot != null && !selectedSceneRoots.Contains(primarySceneRoot)) primarySceneRoot = selectedSceneRoots.LastOrDefault();
    }
    internal void RegisterSceneRootRow(SceneDocument scene) => hierarchyVisibleRows.Add(scene);
    internal void QueueSceneRootClick(SceneDocument scene, bool toggle, bool range) => sceneRootClick = (scene, toggle, range);
    private void AddHierarchyRangeRow(object row)
    {
        if (row is SceneDocument scene)
        {
            if (IsAvailableSceneRoot(scene) && !selectedSceneRoots.Contains(scene)) selectedSceneRoots.Add(scene);
        }
        else if (row is HierarchyContentItem item && IsAvailable(item) && !hierarchyContent.Contains(item)) hierarchyContent.Add(item);
        if (IsSelectedHierarchyContent(row)) TrackHierarchySelection(row);
    }
    private bool AddHierarchyRange(object clicked)
    {
        var first = hierarchyRangeAnchor == null ? -1 : hierarchyVisibleRows.IndexOf(hierarchyRangeAnchor);
        var last = hierarchyVisibleRows.IndexOf(clicked);
        if (first < 0 || last < 0) return false;
        for (var index = Math.Min(first, last); index <= Math.Max(first, last); index++) AddHierarchyRangeRow(hierarchyVisibleRows[index]);
        return true;
    }
    internal SceneDocument? CompleteSceneRootRows()
    {
        if (sceneRootClick is not { } click || !IsAvailableSceneRoot(click.Scene)) return null;
        sceneRootClick = null;
        if (!HierarchyClickChangesSelection(click.Scene, click.Toggle, click.Range))
        {
            if (!click.Range) hierarchyRangeAnchor = click.Scene;
            return click.Scene;
        }
        using var change = BeforeSelectionChange();
        if (!click.Toggle) { hierarchyContent.Clear(); selectedSceneRoots.Clear(); ClearHierarchySelectionOrder(); }
        if (!click.Range || !AddHierarchyRange(click.Scene))
        {
            if (click.Toggle && selectedSceneRoots.Contains(click.Scene)) selectedSceneRoots.Remove(click.Scene);
            else selectedSceneRoots.Add(click.Scene);
            hierarchyRangeAnchor = click.Scene;
        }
        primarySceneRoot = selectedSceneRoots.Contains(click.Scene) ? click.Scene : selectedSceneRoots.LastOrDefault();
        primaryContent = hierarchyContent.Count > 0 ? hierarchyContent[^1] : null;
        ProjectHierarchyContent(primaryContent, primarySceneRoot);
        ContentVersion++;
        return click.Scene;
    }
    private void ClearSceneRootSelection()
    {
        if (selectedSceneRoots.Count > 0) ContentVersion++;
        selectedSceneRoots.Clear(); primarySceneRoot = null; sceneRootClick = null; hierarchyRangeAnchor = null;
        hierarchySelectionOrder.RemoveAll(item => item is SceneDocument);
    }
    internal AssetId[] CaptureSceneRootSelection() => selectedSceneRoots.Select(scene => scene.Asset.Id).ToArray();
    internal void RestoreSceneRootSelection(AssetId[] ids, AssetId? primary, Func<AssetId, SceneDocument?> resolve)
    {
        var next = ids.Select(resolve).OfType<SceneDocument>().Where(IsAvailableSceneRoot).Distinct().ToArray();
        var nextPrimary = primary is { } chosenId ? next.FirstOrDefault(scene => scene.Asset.Id == chosenId) : null;
        if (next.SequenceEqual(selectedSceneRoots) && ReferenceEquals(nextPrimary, primarySceneRoot)) return;
        using var change = BeforeSelectionChange();
        selectedSceneRoots.Clear();
        selectedSceneRoots.AddRange(next);
        primarySceneRoot = nextPrimary;
        if (primarySceneRoot != null) hierarchyRangeAnchor = primarySceneRoot;
        ProjectHierarchyContent(primaryContent, primarySceneRoot);
        ContentVersion++;
    }
}
