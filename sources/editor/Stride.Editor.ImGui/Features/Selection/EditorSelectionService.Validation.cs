namespace Stride.Editor.ImGui;

internal sealed partial class EditorSelectionService
{
    private int selectionChangeDepth;
    private sealed class SelectionChangeScope(EditorSelectionService owner) : IDisposable
    {
        public void Dispose() { if (--owner.selectionChangeDepth == 0) owner.NotifyAfterSelectionChange(); }
    }
    private IDisposable? BeforeSelectionChange(bool changed = true)
    {
        if (!changed) return null;
        var outermost = selectionChangeDepth++ == 0;
        try { if (outermost) context.BeforeSelectionChange(); }
        catch { selectionChangeDepth--; throw; }
        return new SelectionChangeScope(this);
    }

    private bool HierarchyClickChangesSelection(object clicked, bool toggle, bool range)
    {
        List<HierarchyContentItem> content = toggle ? hierarchyContent.ToList() : [];
        List<SceneDocument> scenes = toggle ? selectedSceneRoots.ToList() : [];
        var first = hierarchyRangeAnchor == null ? -1 : hierarchyVisibleRows.IndexOf(hierarchyRangeAnchor);
        var last = hierarchyVisibleRows.IndexOf(clicked);
        if (range && first >= 0 && last >= 0)
        {
            for (var index = Math.Min(first, last); index <= Math.Max(first, last); index++)
            {
                if (hierarchyVisibleRows[index] is SceneDocument rowScene && IsAvailableSceneRoot(rowScene) && !scenes.Contains(rowScene)) scenes.Add(rowScene);
                else if (hierarchyVisibleRows[index] is HierarchyContentItem rowContent && IsAvailable(rowContent) && !content.Contains(rowContent)) content.Add(rowContent);
            }
        }
        else if (clicked is SceneDocument clickedScene)
        {
            if (toggle && scenes.Contains(clickedScene)) scenes.Remove(clickedScene); else scenes.Add(clickedScene);
        }
        else if (clicked is HierarchyContentItem clickedContent)
        {
            if (toggle && content.Contains(clickedContent)) content.Remove(clickedContent); else content.Add(clickedContent);
        }
        var nextContent = clicked is HierarchyContentItem target && content.Contains(target) ? target
            : content.Count > 0 ? content[^1] : (HierarchyContentItem?)null;
        var nextScene = clicked is SceneDocument selected ? scenes.Contains(selected) ? selected : scenes.LastOrDefault() : null;
        return !content.SequenceEqual(hierarchyContent) || !scenes.SequenceEqual(selectedSceneRoots)
            || nextContent != primaryContent || !ReferenceEquals(nextScene, primarySceneRoot)
            || nextContent is { EntityId: { } entity } primary && primary.Document.Selected?.Id != entity;
    }
    internal void ValidatePendingHierarchySelection()
    {
        var changed = contentClick is { } content && IsAvailable(content.Item)
            && HierarchyClickChangesSelection(content.Item, content.Toggle, content.Range)
            || sceneRootClick is { } scene && IsAvailableSceneRoot(scene.Scene)
            && HierarchyClickChangesSelection(scene.Scene, scene.Toggle, scene.Range);
        using var change = BeforeSelectionChange(changed);
    }
}
