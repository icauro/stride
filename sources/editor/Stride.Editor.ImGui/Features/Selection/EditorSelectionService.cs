using Stride.Core.Assets;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal interface IEditorSelectionContext
{
    IReadOnlyCollection<EntityHierarchyDocument>? PresentedOwners { get; }
    long PresentedOwnerVersion { get; }
    long PresentationVersion { get; }
    EntityHierarchyDocument? ActiveHierarchy { get; }
    AssetBrowserState Assets { get; }
    bool IsSelectable(EntityHierarchyDocument owner);
    bool IsCurrentHierarchy(EntityHierarchyDocument owner);
    bool IsSceneRootAvailable(SceneDocument owner);
    void BeforeSelectionChange();
    void AfterSelectionChange();
    IDisposable PreserveNativeSelectionIdentity();
}

/// <summary>Selection across real presented authoring owners; each document retains its own Quantum selection.</summary>
internal sealed partial class EditorSelectionService(IEditorSelectionContext context)
{
    private void NotifyAfterSelectionChange() => context.AfterSelectionChange();
    internal IReadOnlyCollection<EntityHierarchyDocument> NativePresentedOwners => context.PresentedOwners ?? [];
    private readonly List<EntityHierarchyDocument> selectedOwners = [];
    private readonly Dictionary<EntityHierarchyDocument, long> ownerSelectionVersions = new(ReferenceEqualityComparer.Instance);
    private long ownerVersion = -1, presentationVersion = -1;
    internal (EntityHierarchyDocument Document, Guid Entity)? PrimaryIdentity { get; set; }
    internal IReadOnlyList<EntityHierarchyDocument> SelectedOwners
    {
        get
        {
            var owners = context.PresentedOwners;
            if (owners == null)
            {
                InvalidateCache();
                return selectedOwners;
            }
            var changed = ownerVersion != context.PresentedOwnerVersion || presentationVersion != context.PresentationVersion
                || ownerSelectionVersions.Count != owners.Count;
            foreach (var owner in owners)
                if (!ownerSelectionVersions.TryGetValue(owner, out var version) || version != owner.SelectionVersion) changed = true;
            if (!changed) return selectedOwners;
            ownerSelectionVersions.Clear(); selectedOwners.Clear();
            foreach (var owner in owners)
            {
                ownerSelectionVersions[owner] = owner.SelectionVersion;
                if (owner.SelectedCount > 0 && (context.IsSelectable(owner) || owner is SceneDocument root && selectedSceneRoots.Contains(root))) selectedOwners.Add(owner);
            }
            ownerVersion = context.PresentedOwnerVersion; presentationVersion = context.PresentationVersion;
            return selectedOwners;
        }
    }
    internal (EntityHierarchyDocument Document, Entity Entity)? Primary
    {
        get
        {
            var owners = SelectedOwners;
            if (PrimaryIdentity is { } primary && owners.Contains(primary.Document)
                && primary.Document.IsSelected(primary.Entity) && primary.Document.Asset.Hierarchy.Parts.TryGetValue(primary.Entity, out var part))
                return (primary.Document, part.Entity);
            var active = context.ActiveHierarchy;
            if (active?.Selected is { } selected && owners.Contains(active)) return (active, selected);
            var owner = owners.LastOrDefault(); return owner?.Selected is { } entity ? (owner, entity) : null;
        }
    }
    internal bool CanCopy => (SelectedOwners.Count > 0 || HierarchyClipboardFolders.Any())
        && SelectedOwners.All(owner => owner.CanCopyEntities)
        && HierarchyClipboardFolders.All(item => item.Document.CanCopyHierarchyFolder(item.FolderOwner, item.FolderPath!));
    internal bool CanDelete => !HasSelectedPrefabRoot && (SelectedOwners.Count > 0 || HierarchyContent.Any(item => item.FolderPath != null))
        && SelectedOwners.All(owner => owner.CanDeleteSelection)
        && HierarchyContent.Where(item => item.FolderPath != null).All(item => item.Document.CanEditHierarchyFolders(item.FolderOwner));
    internal bool CanDuplicate => (SelectedOwners.Count > 0 || HierarchyContent.Count > 0 || selectedSceneRoots.Count > 0)
        && SelectedOwners.All(owner => owner.CanSave && owner.CanCopyEntities
            && owner.GetSelectedTransformRoots().All(entity => owner.CanCreateEntity(entity.Transform.Parent?.Entity)));

    internal void ClearOtherHierarchySelection(EntityHierarchyDocument owner)
    {
        if (context.PresentedOwners is not { } owners) return;
        using var change = BeforeSelectionChange(owners.Any(other => !ReferenceEquals(other, owner) && other.SelectedCount > 0));
        foreach (var other in owners)
            if (!ReferenceEquals(other, owner) && other.SelectedCount > 0) other.SelectEntity(null);
    }
    internal void SelectHierarchyEntity(EntityHierarchyDocument owner, Entity? entity, bool toggle)
    {
        if (toggle && entity == null) return;
        if (!toggle && owner.SelectedCount == (entity == null ? 0 : 1) && ReferenceEquals(owner.Selected, entity)
            && selectedSceneRoots.Count == 0 && hierarchyContent.All(item => !item.IsRoot && ReferenceEquals(item.Document, owner) && item.FolderPath == null && item.EntityId == entity?.Id)
            && context.PresentedOwners?.Any(other => !ReferenceEquals(other, owner) && other.SelectedCount > 0) != true) return;
        using var change = BeforeSelectionChange();
        ClearContentForEntitySelection(owner, toggle);
        if (!toggle) ClearOtherHierarchySelection(owner);
        owner.SelectEntity(entity, toggle);
        PrimaryIdentity = entity != null && owner.IsSelected(entity.Id) ? (owner, entity.Id) : null;
    }
    internal void ClearPresentedHierarchies()
    {
        using var change = BeforeSelectionChange(hierarchyContent.Count > 0 || selectedSceneRoots.Count > 0
            || context.PresentedOwners?.Any(owner => owner.SelectedCount > 0) == true);
        ClearHierarchyContent();
        if (context.PresentedOwners is not { } owners) return;
        foreach (var owner in owners)
            if (owner.SelectedCount > 0) owner.RestoreSelection([], null);
    }
    internal AssetId[] CaptureAssetSelection() => context.Assets.CaptureSelection();
    internal void RestoreAssetSelection(IEnumerable<AssetId> ids, AssetId? primary)
    {
        var next = context.Assets.NormalizeSelectionIds(ids);
        var nextPrimary = primary is { } id && next.Contains(id) ? primary : next.Length > 0 ? next[0] : (AssetId?)null;
        var current = context.Assets.CaptureSelection();
        if (current.SequenceEqual(next) && context.Assets.Primary == nextPrimary && context.Assets.CaptureContentFolderSelection().Length == 0) return;
        using var change = BeforeSelectionChange();
        context.Assets.RestoreSelection(next, primary);
    }
    internal void ClearAssetSelection() => RestoreAssetSelection([], null);
    internal void ClearAll()
    {
        using var change = BeforeSelectionChange(hierarchyContent.Count > 0 || selectedSceneRoots.Count > 0
            || context.PresentedOwners?.Any(owner => owner.SelectedCount > 0) == true
            || context.Assets.SelectedCount > 0 || context.Assets.CaptureContentFolderSelection().Length > 0);
        ClearPresentedHierarchies(); ClearAssetSelection(); Reset();
    }
    private void InvalidateCache()
    {
        selectedOwners.Clear(); ownerSelectionVersions.Clear(); ownerVersion = presentationVersion = -1;
    }
    internal void Reset()
    {
        ClearHierarchyContent();
        InvalidateCache(); PrimaryIdentity = null;
    }
}
