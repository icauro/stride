using Stride.Assets.Entities;
using Stride.Core;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private Dictionary<Guid, HashSet<string>> emptyHierarchyFolders = [];
    private readonly Dictionary<Guid, string[]> hierarchyFolderCache = [];
    private readonly Dictionary<Guid, Entity[]> hierarchyChildrenCache = [];
    private long cachedChildrenStructure = -1;
    private long cachedFolderStructure = -1, cachedFolderVersion = -1;
    internal long FolderVersion { get; private set; }
    internal bool CanEditHierarchyFolders(Guid? owner) => owner == null ? CanCreateEntity(null)
        : Asset.Hierarchy.Parts.TryGetValue(owner.Value, out var part) && CanCreateEntity(part.Entity);
    internal bool CanRenameHierarchyFolder(Guid? owner, string path) => CanEditHierarchyFolders(owner)
        && GetHierarchyFolders(owner).Contains(path, StringComparer.OrdinalIgnoreCase)
        && HierarchyChildren(owner).Where(entity => InFolderBranch(Asset.Hierarchy.Parts[entity.Id].Folder, path))
            .All(entity => CanEditHierarchyNode(HierarchyGraph.Container.NodeContainer.GetOrCreateNode(Asset.Hierarchy.Parts[entity.Id])[nameof(EntityDesign.Folder)]));
    internal IEnumerable<Entity> HierarchyChildren(Guid? owner) => owner is { } id
        ? Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.Entity.Transform.Children.Select(child => child.Entity) : []
        : Asset.Hierarchy.RootParts;

    internal IReadOnlyList<Entity> GetHierarchyChildrenCached(Guid? owner)
    {
        if (cachedChildrenStructure != StructureVersion)
        { hierarchyChildrenCache.Clear(); cachedChildrenStructure = StructureVersion; }
        var key = owner ?? Guid.Empty;
        if (!hierarchyChildrenCache.TryGetValue(key, out var children))
            hierarchyChildrenCache[key] = children = HierarchyChildren(owner).ToArray();
        return children;
    }

    internal IReadOnlyList<string> GetHierarchyFolders(Guid? owner)
    {
        if (cachedFolderStructure != StructureVersion || cachedFolderVersion != FolderVersion)
        { hierarchyFolderCache.Clear(); cachedFolderStructure = StructureVersion; cachedFolderVersion = FolderVersion; }
        var key = owner ?? Guid.Empty;
        if (hierarchyFolderCache.TryGetValue(key, out var cached)) return cached;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddPath(string? path)
        {
            var prefix = "";
            foreach (var name in (path ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            { prefix = prefix.Length == 0 ? name : prefix + "/" + name; paths.Add(prefix); }
        }
        if (emptyHierarchyFolders.TryGetValue(key, out var empty)) foreach (var path in empty) AddPath(path);
        foreach (var entity in HierarchyChildren(owner)) AddPath(Asset.Hierarchy.Parts[entity.Id].Folder);
        return hierarchyFolderCache[key] = paths.OrderBy(path => path, StringComparer.InvariantCultureIgnoreCase).ToArray();
    }

    internal string CreateHierarchyFolder(Guid? owner, string parentPath = "")
    {
        if (!CanEditHierarchyFolders(owner)) throw new InvalidOperationException("The hierarchy folders cannot be edited.");
        var paths = GetHierarchyFolders(owner);
        if (parentPath.Length > 0 && !paths.Contains(parentPath, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("The parent folder no longer exists.");
        var prefix = parentPath.Length == 0 ? "" : parentPath + "/";
        var siblings = paths.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !path[prefix.Length..].Contains('/')).Select(path => path[prefix.Length..]);
        var name = NamingHelper.ComputeNewName("Folder", siblings, value => value);
        ChangeHierarchyFolders(owner, [], paths.Append(prefix + name).ToArray(), "Create folder");
        return prefix + name;
    }

    internal void RenameHierarchyFolder(Guid? owner, string path, string name)
    {
        if (!CanRenameHierarchyFolder(owner, path) || string.IsNullOrWhiteSpace(name) || name.Contains('/')) throw new InvalidOperationException("Invalid folder name.");
        var paths = GetHierarchyFolders(owner);
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("The folder no longer exists.");
        var slash = path.LastIndexOf('/'); var prefix = slash < 0 ? "" : path[..(slash + 1)]; var replacement = prefix + name;
        if (!string.Equals(path, replacement, StringComparison.OrdinalIgnoreCase) && paths.Contains(replacement, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unable to rename the folder '{path}' to '{name}'. A folder with the same name already exists.");
        if (path == replacement) return;
        string Rename(string value) => string.Equals(value, path, StringComparison.OrdinalIgnoreCase) ? replacement
            : value.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) ? replacement + value[path.Length..] : value;
        var changes = HierarchyChildren(owner).Select(entity => Asset.Hierarchy.Parts[entity.Id]).Where(part => Rename(part.Folder ?? "") != (part.Folder ?? ""))
            .Select(part => (part, Rename(part.Folder ?? ""))).ToArray();
        ChangeHierarchyFolders(owner, changes, paths.Select(Rename).ToArray(), "Rename folder");
    }

    internal bool CanMoveSelectionToFolder(Guid? owner) => CanDeleteSelection && CanMoveEntitiesToFolder(GetSelectedTransformRoots(), owner);
    internal void MoveSelectionToFolder(Guid? owner, string path)
    {
        MoveEntitiesToFolder(GetSelectedTransformRoots(), owner, path, false);
    }

    private void ChangeHierarchyFolders(Guid? owner, (EntityDesign Part, string Folder)[] changes, string[] afterFolders, string action)
    {
        EndEdits(); EnsureHierarchyGraph(); var key = owner ?? Guid.Empty;
        var beforeFolders = GetHierarchyFolders(owner).ToArray();
        var members = changes.Select(change => hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(change.Part)[nameof(EntityDesign.Folder)]).ToArray();
        var before = changes.Select(change => change.Part.Folder).ToArray();
        if (members.Any(member => !CanEditHierarchyNode(member))) throw new InvalidOperationException("This hierarchy folder is sealed.");
        if (RequiresHierarchyJournal && changes.Length > 0)
        {
            RecordHierarchyFolderGroup(() =>
            {
                ApplyInheritedGraphMutation(() =>
                {
                    for (var index = 0; index < members.Length; index++) members[index].Update(changes[index].Folder);
                }, action);
                ChangeHierarchyFolderOwnerPaths([new(key, beforeFolders, afterFolders)], action);
            }, action);
            return;
        }
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Apply(bool forward)
        {
            for (var index = 0; index < members.Length; index++) members[index].Update(forward ? changes[index].Folder : before[index]);
            emptyHierarchyFolders[key] = new HashSet<string>(forward ? afterFolders : beforeFolders, StringComparer.OrdinalIgnoreCase);
            FolderVersion++; revision = forward ? afterRevision : beforeRevision;
        }
        try
        {
            Apply(true);
            using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(() => Apply(false), () => Apply(true)));
        }
        catch { Apply(false); throw; }
        LastAction = action;
    }
}
