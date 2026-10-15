using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal Entity[] HierarchyFolderEntities(Guid? owner, string path)
    {
        var roots = HierarchyChildren(owner).Where(entity => path.Length == 0 || InFolderBranch(Asset.Hierarchy.Parts[entity.Id].Folder, path)).ToArray();
        var entities = roots.GroupBy(entity => (Asset.Hierarchy.Parts[entity.Id].Folder ?? "").Trim('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in GetHierarchyFolders(owner))
        {
            if (path.Length != 0 && !folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = folder.LastIndexOf('/');
            var parent = slash < 0 ? "" : folder[..slash];
            if (!children.TryGetValue(parent, out var folders)) children.Add(parent, folders = []);
            folders.Add(folder);
        }
        foreach (var folders in children.Values)
            folders.Sort((left, right) => StringComparer.InvariantCultureIgnoreCase.Compare(
                left[(left.LastIndexOf('/') + 1)..], right[(right.LastIndexOf('/') + 1)..]));
        var result = new List<Entity>(roots.Length);
        var pending = new Stack<(string Folder, bool Emit)>();
        pending.Push((path, false));
        while (pending.TryPop(out var current))
        {
            if (current.Emit)
            {
                if (entities.TryGetValue(current.Folder, out var direct)) result.AddRange(direct);
                continue;
            }
            // Native Children contains sorted folders first, then the actual direct sibling entities.
            pending.Push((current.Folder, true));
            if (children.TryGetValue(current.Folder, out var subfolders))
                for (var index = subfolders.Count - 1; index >= 0; index--) pending.Push((subfolders[index], false));
        }
        return result.ToArray();
    }
}
