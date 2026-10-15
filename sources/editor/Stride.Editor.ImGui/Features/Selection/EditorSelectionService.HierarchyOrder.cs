using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed record HierarchySelectionOrderSnapshot(AssetId AssetId, Guid? EntityId, Guid? FolderOwner,
    string? FolderPath, bool IsPrefabRoot, bool IsSceneRoot);

internal sealed partial class EditorSelectionService
{
    private readonly List<object> hierarchySelectionOrder = [];
    private readonly HashSet<object> hierarchyOrderedItems = [];
    private readonly HashSet<object> hierarchyOrderSelection = [];
    private long hierarchyOrderVersion = -1;
    private void ClearHierarchySelectionOrder()
    {
        hierarchySelectionOrder.Clear(); hierarchyOrderedItems.Clear(); hierarchyOrderSelection.Clear();
        hierarchyOrderVersion = -1;
    }
    private bool IsSelectedHierarchyContent(object item) => item is SceneDocument scene
        ? selectedSceneRoots.Contains(scene) && IsAvailableSceneRoot(scene)
        : item is HierarchyContentItem content && hierarchyContent.Contains(content) && IsAvailable(content);
    private void TrackHierarchySelection(object item)
    {
        if (hierarchyOrderedItems.Add(item)) { hierarchySelectionOrder.Add(item); hierarchyOrderVersion = -1; }
    }
    private void SynchronizeHierarchySelectionOrder(bool force = false)
    {
        if (!force && hierarchyOrderVersion == ContentVersion) return;
        hierarchyOrderSelection.Clear();
        foreach (var item in hierarchyContent) hierarchyOrderSelection.Add(item);
        foreach (var scene in selectedSceneRoots) if (IsAvailableSceneRoot(scene)) hierarchyOrderSelection.Add(scene);
        hierarchySelectionOrder.RemoveAll(item => !hierarchyOrderSelection.Contains(item));
        hierarchyOrderedItems.Clear(); hierarchyOrderedItems.UnionWith(hierarchySelectionOrder);
        foreach (var item in hierarchyContent) TrackHierarchySelection(item);
        foreach (var scene in selectedSceneRoots) if (hierarchyOrderSelection.Contains(scene)) TrackHierarchySelection(scene);
        hierarchyOrderVersion = ContentVersion;
    }
    internal object? FirstHierarchySelectedContent
    {
        get { SynchronizeHierarchyContent(); SynchronizeHierarchySelectionOrder(); return hierarchySelectionOrder.FirstOrDefault(); }
    }
    internal object? LastHierarchySelectedContent
    {
        get { SynchronizeHierarchyContent(); SynchronizeHierarchySelectionOrder(); return hierarchySelectionOrder.LastOrDefault(); }
    }
    internal static HierarchySelectionOrderSnapshot DescribeHierarchySelection(object item) => item is SceneDocument scene
        ? new(scene.Asset.Id, null, null, null, false, true)
        : item is HierarchyContentItem content
            ? new(content.Document.Asset.Id, content.EntityId, content.FolderOwner, content.FolderPath, content.IsRoot, false)
            : throw new ArgumentException("This is not native hierarchy selected content.", nameof(item));
    internal HierarchySelectionOrderSnapshot[] CaptureHierarchySelectionOrder()
    {
        SynchronizeHierarchyContent(); SynchronizeHierarchySelectionOrder();
        return hierarchySelectionOrder.Select(DescribeHierarchySelection).ToArray();
    }
    internal void RestoreHierarchySelectionOrder(IEnumerable<HierarchySelectionOrderSnapshot> saved,
        Func<AssetId, EntityHierarchyDocument?> resolve)
    {
        var next = new List<object>();
        foreach (var item in saved)
        {
            var owner = resolve(item.AssetId);
            object? identity = item.IsSceneRoot ? owner as SceneDocument : owner == null ? null
                : new HierarchyContentItem(owner, item.EntityId, item.FolderOwner, item.FolderPath, item.IsPrefabRoot);
            if (identity != null && IsSelectedHierarchyContent(identity) && !next.Contains(identity)) next.Add(identity);
        }
        foreach (var item in hierarchySelectionOrder) if (IsSelectedHierarchyContent(item) && !next.Contains(item)) next.Add(item);
        if (next.SequenceEqual(hierarchySelectionOrder)) return;
        ClearHierarchySelectionOrder();
        foreach (var item in next) TrackHierarchySelection(item);
        ContentVersion++;
    }
}
