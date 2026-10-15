using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Engine;
using Stride.Core;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private Entity[] HierarchyFolderClipboardRoots(Guid? owner, string path) => HierarchyFolderEntities(owner, path);

    internal bool CanCopyHierarchyFolder(Guid? owner, string path) => GetHierarchyFolders(owner).Contains(path, StringComparer.OrdinalIgnoreCase)
        && CanCopyHierarchyRoots(HierarchyFolderClipboardRoots(owner, path), allowEmpty: true);

    internal bool CanCutHierarchyFolder(Guid? owner, string path) => SessionHistory != null && CanCopyHierarchyFolder(owner, path) && CanEditHierarchyFolders(owner)
        && HierarchyFolderClipboardRoots(owner, path).All(CanDeleteEntity);
    internal bool CanDuplicateHierarchyFolder(Guid? owner, string path) => SessionHistory != null && CanCopyHierarchyFolder(owner, path)
        && HierarchyFolderClipboardRoots(owner, path).All(entity => CanCreateEntity(entity.Transform.Parent?.Entity));

    internal string CopyHierarchyFolder(Guid? owner, string path)
    {
        if (!CanCopyHierarchyFolder(owner, path)) throw new InvalidOperationException("The selected folder cannot be copied.");
        EndEdits(); EnsureHierarchyGraph();
        if (!CanCopyHierarchyFolder(owner, path)) throw new InvalidOperationException("The selected clipboard closure changed.");
        var roots = HierarchyFolderClipboardRoots(owner, path);
        var hierarchy = AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>.CloneSubHierarchies(
            HierarchyGraph.Container.NodeContainer, Asset, roots.Select(entity => entity.Id), SubHierarchyCloneFlags.None, out _);
        foreach (var part in hierarchy.Parts.Values) part.Folder = null;
        var parentPrefixLength = path.LastIndexOf('/') + 1;
        foreach (var root in roots)
            hierarchy.Parts[root.Id].Folder = Asset.Hierarchy.Parts[root.Id].Folder[parentPrefixLength..];
        return HierarchyFolderClipboardMetadata.Attach(EntityClipboardService.CopyFromAsset(HierarchyGraph, Asset.Id, hierarchy, false), [(this, owner, path)]);
    }

    internal void CutHierarchyFolder(Guid? owner, string path)
    {
        if (!CanCutHierarchyFolder(owner, path)) throw new InvalidOperationException("The selected folder cannot be cut.");
        var roots = HierarchyFolderClipboardRoots(owner, path);
        if (roots.Length > 0) CutHierarchyRootsWithJournal(roots);
    }

    internal void DuplicateHierarchyFolder(Guid? owner, string path)
    {
        if (!CanDuplicateHierarchyFolder(owner, path)) throw new InvalidOperationException("The selected folder contents cannot be duplicated.");
        var roots = HierarchyFolderClipboardRoots(owner, path);
        if (roots.Length > 0) DuplicateEntitiesWithJournal(roots);
    }

    internal bool HierarchyFolderHasEntities(Guid? owner, string path) => HierarchyFolderClipboardRoots(owner, path).Length > 0;

    internal bool CanPasteHierarchyFolder(string text, Guid? owner, string path)
    {
        if (text.StartsWith("# StrideHierarchyFoldersV1:", StringComparison.Ordinal))
            return GetHierarchyFolders(owner).Contains(path, StringComparer.OrdinalIgnoreCase) && CanPasteHierarchyContent(text, [(owner, path)]);
        if (SessionHistory == null || string.IsNullOrWhiteSpace(text) || text.Length > 16 * 1024 * 1024
            || !GetHierarchyFolders(owner).Contains(path, StringComparer.OrdinalIgnoreCase)) return false;
        var parent = owner is { } id && Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.Entity : null;
        return (owner == null || parent != null) && CanCreateEntity(parent)
            && EntityClipboardService.CanPaste(text, Asset.GetType(), parent == null ? typeof(AssetCompositeHierarchyData<EntityDesign, Entity>) : typeof(Entity),
                typeof(AssetCompositeHierarchyData<EntityDesign, Entity>));
    }

    internal void PasteHierarchyFolder(string text, Guid? owner, string path)
    {
        if (!CanPasteHierarchyFolder(text, owner, path)) throw new InvalidOperationException("The folder cannot accept the copied hierarchy.");
        if (text.StartsWith("# StrideHierarchyFoldersV1:", StringComparison.Ordinal)) PasteHierarchyContent(text, [(owner, path)]);
        else PasteEntitiesWithJournal(text, [owner is { } id ? Asset.Hierarchy.Parts[id].Entity : null], path);
    }
}
