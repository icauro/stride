namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal void RemoveHierarchyFolders(IEnumerable<(Guid? Owner, string Path)> selected)
    {
        foreach (var group in selected.GroupBy(folder => folder.Owner))
        {
            if (group.Key is { } owner && !Asset.Hierarchy.Parts.ContainsKey(owner)) continue;
            if (!CanEditHierarchyFolders(group.Key)) throw new InvalidOperationException("The hierarchy folders cannot be edited.");
            var before = GetHierarchyFolders(group.Key).ToArray();
            var after = before.Where(path => !group.Any(folder => InFolderBranch(path, folder.Path))).ToArray();
            if (after.Length == before.Length) continue;
            if (HierarchyChildren(group.Key).Any(entity => group.Any(folder => InFolderBranch(Asset.Hierarchy.Parts[entity.Id].Folder, folder.Path))))
                throw new InvalidOperationException("Delete the folder entities before deleting their folder.");
            ChangeHierarchyFolders(group.Key, [], after, "Delete folders");
        }
    }
}
