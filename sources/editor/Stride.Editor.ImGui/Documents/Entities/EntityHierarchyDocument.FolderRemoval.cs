using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal void ApplyInheritedHierarchyReconciliation(Action reconcile, string action)
    {
        EndEdits();
        var folders = CapturePartRemovalFolders(Asset.Hierarchy.Parts.Values.Select(part => part.Entity));
        RecordHierarchyFolderGroup(() =>
        {
            ApplyInheritedGraphMutation(reconcile, action);
            RetainPartRemovalFolders(folders, action);
        }, action);
    }

    private Dictionary<Guid, string[]> CapturePartRemovalFolders(IEnumerable<Entity> roots)
    {
        var folders = new Dictionary<Guid, string[]>();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(Asset.Hierarchy.Parts[root.Id].Folder)) continue;
            var owner = root.Transform.Parent?.Entity.Id;
            folders.TryAdd(owner ?? Guid.Empty, GetHierarchyFolders(owner).ToArray());
        }
        return folders;
    }

    private void RetainPartRemovalFolders(Dictionary<Guid, string[]> folders, string action)
    {
        var changes = new List<FolderOwnerPathsChange>();
        foreach (var (key, previousPaths) in folders)
        {
            // A removed entity takes its owned folder tree with it. Only the
            // surviving original parent/root keeps the native empty folder nodes.
            if (key != Guid.Empty && !Asset.Hierarchy.Parts.ContainsKey(key)) continue;
            var present = GetHierarchyFolders(key == Guid.Empty ? null : key);
            var missing = previousPaths.Where(path => !present.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (missing.Length == 0) continue;
            var before = emptyHierarchyFolders.TryGetValue(key, out var authored) ? authored.ToArray() : [];
            changes.Add(new(key, before, before.Concat(missing).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        if (changes.Count > 0) ChangeHierarchyFolderOwnerPaths(changes.ToArray(), action);
    }
}
