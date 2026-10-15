namespace Stride.Editor.ImGui;

internal sealed record HierarchyMovePresentationSelection(HierarchyContentItem[] Content, HierarchyContentItem? Primary,
    HierarchyContentItem? Anchor, object? RangeAnchor, SceneDocument[] SceneRoots, SceneDocument? PrimaryScene, object[] Order);

internal sealed partial class EditorSelectionService
{
    internal HierarchyMovePresentationSelection CaptureHierarchyMovePresentation()
    {
        SynchronizeHierarchyContent();
        return new(hierarchyContent.ToArray(), primaryContent, contentAnchor, hierarchyRangeAnchor, selectedSceneRoots.ToArray(), primarySceneRoot,
            hierarchySelectionOrder.ToArray());
    }

    internal void ApplyHierarchyMovePresentation(HierarchyMovePresentationSelection previous,
        IReadOnlyDictionary<HierarchyContentItem, HierarchyContentItem> mapping, bool filterUnmoved, bool validate = true)
    {
        HierarchyContentItem Remap(HierarchyContentItem item) => mapping.TryGetValue(item, out var target) ? target : item;
        var next = previous.Content.Where(item => !filterUnmoved || mapping.ContainsKey(item)).Select(Remap).Where(IsAvailable).Distinct().ToArray();
        SceneDocument[] roots = filterUnmoved ? [] : previous.SceneRoots.Where(IsAvailableSceneRoot).ToArray();
        var nextPrimary = !filterUnmoved && previous.Primary is { } primary && next.Contains(Remap(primary)) ? Remap(primary)
            : next.Length > 0 ? next[^1] : (HierarchyContentItem?)null;
        var nextScene = !filterUnmoved && roots.Contains(previous.PrimaryScene!) ? previous.PrimaryScene : null;
        var changed = !next.SequenceEqual(hierarchyContent) || !roots.SequenceEqual(selectedSceneRoots)
            || nextPrimary != primaryContent || !ReferenceEquals(nextScene, primarySceneRoot);
        using var change = BeforeSelectionChange(validate && changed);
        var before = new HierarchyMovePresentationSelection(hierarchyContent.ToArray(), primaryContent, contentAnchor,
            hierarchyRangeAnchor, selectedSceneRoots.ToArray(), primarySceneRoot, hierarchySelectionOrder.ToArray());
        var previousIdentity = PrimaryIdentity;
        var previousProjectionVersions = contentProjectionVersions.ToArray();
        var previousRootStructures = rootProjectionStructures.ToArray();
        var previousPresentationVersion = rootProjectionPresentationVersion;
        var owners = context.PresentedOwners?.Select(owner => (Owner: owner, Selection: owner.SnapshotSelection())).ToArray() ?? [];
        try
        {
            hierarchyContent.Clear(); hierarchyContent.AddRange(next);
            selectedSceneRoots.Clear(); selectedSceneRoots.AddRange(roots);
            primaryContent = nextPrimary; primarySceneRoot = nextScene;
            ClearHierarchySelectionOrder();
            foreach (var item in previous.Order)
            {
                object? remapped = item is HierarchyContentItem content
                    ? !filterUnmoved || mapping.ContainsKey(content) ? (object)Remap(content) : null : filterUnmoved ? null : item;
                if (remapped != null && IsSelectedHierarchyContent(remapped)) TrackHierarchySelection(remapped);
            }
            contentAnchor = !filterUnmoved && previous.Anchor is { } anchor && next.Contains(Remap(anchor)) ? Remap(anchor) : nextPrimary;
            hierarchyRangeAnchor = !filterUnmoved && previous.RangeAnchor is SceneDocument rangeScene && roots.Contains(rangeScene) ? rangeScene
                : !filterUnmoved && previous.RangeAnchor is HierarchyContentItem range && next.Contains(Remap(range)) ? Remap(range)
                : (object?)nextScene ?? nextPrimary;
            contentProjectionVersions.Clear();
            ProjectHierarchyContent(nextPrimary, nextScene);
            if (changed) ContentVersion++;
        }
        catch (Exception failure)
        {
            hierarchyContent.Clear(); hierarchyContent.AddRange(before.Content);
            selectedSceneRoots.Clear(); selectedSceneRoots.AddRange(before.SceneRoots);
            primaryContent = before.Primary; primarySceneRoot = before.PrimaryScene;
            contentAnchor = before.Anchor; hierarchyRangeAnchor = before.RangeAnchor; contentProjectionVersions.Clear();
            ClearHierarchySelectionOrder();
            foreach (var item in before.Order) TrackHierarchySelection(item);
            var errors = new List<Exception> { failure };
            foreach (var owner in owners)
                try { owner.Owner.RestoreSelection(owner.Selection.Ids, owner.Selection.PrimaryId); }
                catch (Exception recovery) { errors.Add(recovery); }
            PrimaryIdentity = previousIdentity;
            foreach (var entry in previousProjectionVersions) contentProjectionVersions[entry.Key] = entry.Value;
            rootProjectionStructures.Clear();
            foreach (var entry in previousRootStructures) rootProjectionStructures[entry.Key] = entry.Value;
            rootProjectionPresentationVersion = previousPresentationVersion;
            if (errors.Count > 1) throw new AggregateException("Hierarchy selection fixup and restoration failed.", errors);
            throw;
        }
    }
}
