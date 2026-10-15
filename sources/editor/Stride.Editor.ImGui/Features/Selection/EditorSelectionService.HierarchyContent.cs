namespace Stride.Editor.ImGui;

internal readonly record struct HierarchyContentItem(EntityHierarchyDocument Document, Guid? EntityId, Guid? FolderOwner, string? FolderPath, bool IsRoot = false)
{
    internal static HierarchyContentItem Entity(EntityHierarchyDocument owner, Guid id) => new(owner, id, null, null);
    internal static HierarchyContentItem Folder(EntityHierarchyDocument owner, Guid? parent, string path) => new(owner, null, parent, path);
    internal static HierarchyContentItem Root(PrefabDocument owner) => new(owner, null, null, null, true);
}

internal sealed partial class EditorSelectionService
{
    private readonly List<HierarchyContentItem> hierarchyContent = [];
    private readonly List<HierarchyContentItem> hierarchyRows = [];
    private readonly Dictionary<EntityHierarchyDocument, long> contentProjectionVersions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<EntityHierarchyDocument, long> rootProjectionStructures = new(ReferenceEqualityComparer.Instance);
    private long rootProjectionPresentationVersion = -1;
    private long contentOwnerVersion = -1;
    private HierarchyContentItem? contentAnchor;
    private HierarchyContentItem? primaryContent;
    private (HierarchyContentItem Item, bool Toggle, bool Range)? contentClick;
    internal long ContentVersion { get; private set; }
    internal bool HasPendingHierarchyClick => contentClick != null;
    internal bool HasSelectedPrefabRoot { get { SynchronizeHierarchyContent(); return hierarchyContent.Any(item => item.IsRoot); } }
    internal bool PrimaryContentIsRoot { get { SynchronizeHierarchyContent(); return primaryContent?.IsRoot == true || primarySceneRoot != null; } }
    internal HierarchyContentItem? PrimaryHierarchyContent { get { SynchronizeHierarchyContent(); return primarySceneRoot == null ? primaryContent : null; } }
    internal IReadOnlyList<HierarchyContentItem> HierarchyContent { get { SynchronizeHierarchyContent(); return hierarchyContent; } }

    private bool IsAvailable(HierarchyContentItem item) => context.IsSelectable(item.Document)
        && (item.IsRoot ? item.Document is PrefabDocument && context.IsCurrentHierarchy(item.Document) && context.PresentedOwners?.Contains(item.Document) == true
            : item.EntityId is { } id ? item.Document.Asset.Hierarchy.Parts.ContainsKey(id)
            : item.FolderPath != null && item.Document.GetHierarchyFolders(item.FolderOwner).Contains(item.FolderPath, StringComparer.OrdinalIgnoreCase));

    private void SynchronizeHierarchyContent()
    {
        var owners = context.PresentedOwners;
        if (owners == null) { ClearHierarchyContent(false); return; }
        if (contentOwnerVersion != context.PresentedOwnerVersion)
        {
            foreach (var removed in contentProjectionVersions.Keys.Where(owner => !owners.Contains(owner)).ToArray()) contentProjectionVersions.Remove(removed);
            contentOwnerVersion = context.PresentedOwnerVersion;
        }
        var changed = hierarchyContent.RemoveAll(item => !owners.Contains(item.Document) || !IsAvailable(item)) > 0;
        if (selectedSceneRoots.Count > 0 && rootProjectionPresentationVersion != context.PresentationVersion)
        { ProjectHierarchyContent(primaryContent, primarySceneRoot); changed = true; }
        foreach (var item in hierarchyContent)
            if (item.IsRoot && rootProjectionStructures.GetValueOrDefault(item.Document, -1) != item.Document.StructureVersion
                && contentProjectionVersions.GetValueOrDefault(item.Document, -1) == item.Document.SelectionVersion)
            {
                // Existing root content follows genuine hierarchy changes; an explicit entity selection still wins.
                ProjectHierarchyContent(primaryContent ?? item);
                changed = true;
                break;
            }
        foreach (var scene in selectedSceneRoots)
            if (IsAvailableSceneRoot(scene)
                && rootProjectionStructures.GetValueOrDefault(scene, -1) != scene.StructureVersion
                && contentProjectionVersions.GetValueOrDefault(scene, -1) == scene.SelectionVersion)
            { ProjectHierarchyContent(primaryContent, primarySceneRoot); changed = true; break; }
        foreach (var owner in owners)
        {
            if (contentProjectionVersions.GetValueOrDefault(owner, -1) == owner.SelectionVersion) continue;
            changed |= hierarchyContent.RemoveAll(item => ReferenceEquals(item.Document, owner)) > 0;
            if (context.IsSelectable(owner)) foreach (var id in owner.SelectedIds)
            { hierarchyContent.Add(HierarchyContentItem.Entity(owner, id)); changed = true; }
            contentProjectionVersions[owner] = owner.SelectionVersion;
        }
        if (!hierarchyContent.Any(item => item.IsRoot) && selectedSceneRoots.Count == 0) rootProjectionStructures.Clear();
        if (changed) ContentVersion++;
        if (contentAnchor != null && !IsAvailable(contentAnchor.Value)) contentAnchor = null;
        if (primaryContent is { } previous && !hierarchyContent.Contains(previous)) primaryContent = null;
        if (primaryContent == null && hierarchyContent.Count > 0)
        {
            var entityPrimary = PrimaryIdentity is { } selected ? HierarchyContentItem.Entity(selected.Document, selected.Entity) : (HierarchyContentItem?)null;
            primaryContent = entityPrimary is { } item && hierarchyContent.Contains(item) ? item : hierarchyContent[^1];
        }
        SynchronizeHierarchySelectionOrder();
    }

    internal void BeginHierarchyRows() { SynchronizeHierarchyContent(); hierarchyRows.Clear(); contentClick = null; BeginSceneRootRows(); }
    internal void RegisterHierarchyRow(HierarchyContentItem item) { hierarchyRows.Add(item); hierarchyVisibleRows.Add(item); }
    internal bool IsContentSelected(HierarchyContentItem item) => hierarchyContent.Contains(item);
    internal void QueueHierarchyClick(HierarchyContentItem item, bool toggle, bool range) => contentClick = (item, toggle, range);
    internal HierarchyContentItem? CompleteHierarchyRows()
    {
        if (contentClick is not { } click || !IsAvailable(click.Item)) return null;
        contentClick = null;
        if (!HierarchyClickChangesSelection(click.Item, click.Toggle, click.Range))
        {
            if (!click.Range) { contentAnchor = click.Item; hierarchyRangeAnchor = click.Item; }
            return click.Item;
        }
        using var change = BeforeSelectionChange();
        if (!click.Toggle) { hierarchyContent.Clear(); selectedSceneRoots.Clear(); ClearHierarchySelectionOrder(); }
        if (click.Range && AddHierarchyRange(click.Item)) { }
        else
        {
            if (click.Toggle && hierarchyContent.Contains(click.Item)) hierarchyContent.Remove(click.Item);
            else hierarchyContent.Add(click.Item);
            contentAnchor = click.Item;
            hierarchyRangeAnchor = click.Item;
        }
        primarySceneRoot = null;
        primaryContent = hierarchyContent.Contains(click.Item) ? click.Item : hierarchyContent.Count > 0 ? hierarchyContent[^1] : null;
        ProjectHierarchyContent(primaryContent ?? click.Item);
        ContentVersion++;
        return click.Item;
    }

    private void ProjectHierarchyContent(HierarchyContentItem? primary, SceneDocument? primaryScene = null)
    {
        SynchronizeHierarchySelectionOrder(force: true);
        if (context.PresentedOwners is not { } owners) return;
        rootProjectionStructures.Clear();
        rootProjectionPresentationVersion = context.PresentationVersion;
        foreach (var owner in owners)
        {
            var ids = new HashSet<Guid>();
            if (owner is SceneDocument scene && selectedSceneRoots.Contains(scene))
            {
                foreach (var entity in owner.HierarchyFolderEntities(null, "")) ids.Add(entity.Id);
                rootProjectionStructures[owner] = owner.StructureVersion;
            }
            foreach (var item in hierarchyContent.Where(item => ReferenceEquals(item.Document, owner)))
            {
                if (item.EntityId is { } id) ids.Add(id);
                // Native Root.InnerSubEntities traverses folders, stopping at each immediate transform root.
                else if (item.IsRoot)
                {
                    foreach (var entity in owner.HierarchyFolderEntities(null, "")) ids.Add(entity.Id);
                    rootProjectionStructures[owner] = owner.StructureVersion;
                }
                else foreach (var entity in owner.HierarchyFolderEntities(item.FolderOwner, item.FolderPath!)) ids.Add(entity.Id);
            }
            var selectedPrimary = primary is { } actual && ReferenceEquals(actual.Document, owner) && actual.EntityId is { } chosen && ids.Contains(chosen) ? chosen
                : owner.Selected?.Id is { } existing && ids.Contains(existing) ? existing : ids.Count > 0 ? ids.First() : (Guid?)null;
            owner.RestoreSelection(ids, selectedPrimary);
            contentProjectionVersions[owner] = owner.SelectionVersion;
        }
        var primaryOwner = primaryScene ?? primary?.Document;
        PrimaryIdentity = primaryOwner?.Selected is { } selected ? (primaryOwner, selected.Id) : null;
    }

    private void ClearContentForEntitySelection(EntityHierarchyDocument owner, bool toggle)
    {
        primarySceneRoot = null;
        if (!toggle) ClearHierarchyContent();
        else { hierarchyContent.RemoveAll(item => ReferenceEquals(item.Document, owner)); contentProjectionVersions.Remove(owner); ContentVersion++; }
    }
    internal void ClearHierarchyContent(bool validate = true)
    {
        using var change = BeforeSelectionChange(validate && (hierarchyContent.Count > 0 || selectedSceneRoots.Count > 0));
        ClearSceneRootSelection();
        if (hierarchyContent.Count > 0) ContentVersion++;
        hierarchyContent.Clear(); contentProjectionVersions.Clear(); rootProjectionStructures.Clear(); contentAnchor = primaryContent = null; contentClick = null;
        ClearHierarchySelectionOrder();
        contentOwnerVersion = -1;
    }
    internal HierarchyContentSelectionSnapshot[] CaptureHierarchyContent() => HierarchyContent.Select(item =>
        new HierarchyContentSelectionSnapshot(item.Document.Asset.Id, item.EntityId, item.FolderOwner, item.FolderPath, item.IsRoot)).ToArray();
    internal HierarchyContentSelectionSnapshot? CapturePrimaryHierarchyContent() => primaryContent is { } item
        ? new(item.Document.Asset.Id, item.EntityId, item.FolderOwner, item.FolderPath, item.IsRoot) : null;
    internal void RestoreHierarchyContent(IEnumerable<HierarchyContentSelectionSnapshot> snapshot, Func<Stride.Core.Assets.AssetId, EntityHierarchyDocument?> resolve,
        HierarchyContentSelectionSnapshot? primary = null)
    {
        var next = new List<HierarchyContentItem>();
        foreach (var saved in snapshot)
            if (resolve(saved.AssetId) is { } owner)
            {
                var item = new HierarchyContentItem(owner, saved.EntityId, saved.FolderOwner, saved.FolderPath, saved.IsRoot);
                if (IsAvailable(item)) next.Add(item);
            }
        var preferred = primary != null && resolve(primary.AssetId) is { } preferredOwner
            ? new HierarchyContentItem(preferredOwner, primary.EntityId, primary.FolderOwner, primary.FolderPath, primary.IsRoot) : (HierarchyContentItem?)null;
        var nextPrimary = preferred is { } target && next.Contains(target) ? target : next.Count > 0 ? next[^1] : (HierarchyContentItem?)null;
        if (next.SequenceEqual(hierarchyContent) && nextPrimary == primaryContent && selectedSceneRoots.Count == 0) return;
        using var change = BeforeSelectionChange();
        ClearHierarchyContent();
        hierarchyContent.AddRange(next);
        if (hierarchyContent.Count > 0)
        {
            primaryContent = nextPrimary;
            contentAnchor = primaryContent; hierarchyRangeAnchor = primaryContent; ProjectHierarchyContent(primaryContent.Value);
        }
        ContentVersion++;
    }
    internal void SetHierarchyContent(IEnumerable<HierarchyContentItem> items)
    {
        var next = items.Where(IsAvailable).Distinct().ToArray();
        var nextPrimary = next.Length > 0 ? next[^1] : (HierarchyContentItem?)null;
        if (next.SequenceEqual(hierarchyContent) && nextPrimary == primaryContent && selectedSceneRoots.Count == 0) return;
        using var change = BeforeSelectionChange();
        ClearHierarchyContent();
        hierarchyContent.AddRange(next);
        if (hierarchyContent.Count > 0) { contentAnchor = primaryContent = hierarchyContent[^1]; hierarchyRangeAnchor = contentAnchor; ProjectHierarchyContent(contentAnchor.Value); }
        ContentVersion++;
    }
}
