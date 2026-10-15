namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal sealed record FolderOwnerPathsChange(Guid Owner, string[] Before, string[] After);

    internal bool CanMoveHierarchyFolder(Guid? sourceOwner, string sourcePath, Guid? targetOwner, string targetPath, bool allowSameLocation = false)
    {
        if (!CanEditHierarchyFolders(sourceOwner) || !CanEditHierarchyFolders(targetOwner)
            || !GetHierarchyFolders(sourceOwner).Contains(sourcePath, StringComparer.OrdinalIgnoreCase)
            || targetPath.Length > 0 && !GetHierarchyFolders(targetOwner).Contains(targetPath, StringComparer.OrdinalIgnoreCase)) return false;
        var name = sourcePath[(sourcePath.LastIndexOf('/') + 1)..];
        var replacement = targetPath.Length == 0 ? name : targetPath + "/" + name;
        var sameLocation = sourceOwner == targetOwner && string.Equals(sourcePath, replacement, StringComparison.OrdinalIgnoreCase);
        if ((sameLocation && !allowSameLocation) || sourceOwner == targetOwner
            && (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) || targetPath.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase))) return false;
        if (!sameLocation && GetHierarchyFolders(targetOwner).Contains(replacement, StringComparer.OrdinalIgnoreCase)) return false;
        var entities = HierarchyFolderEntities(sourceOwner, sourcePath);
        return entities.Length == 0 || CanMoveEntitiesToFolder(entities, targetOwner);
    }

    internal IReadOnlyList<Stride.Engine.Entity> MoveHierarchyFolder(Guid? sourceOwner, string sourcePath, Guid? targetOwner, string targetPath, bool keepWorld,
        int? insertionIndex = null, bool allowSameLocation = false)
    {
        if (!CanMoveHierarchyFolder(sourceOwner, sourcePath, targetOwner, targetPath, allowSameLocation)) throw new InvalidOperationException("This hierarchy folder cannot be moved here.");
        var targetCount = targetOwner is { } targetId ? Asset.Hierarchy.Parts[targetId].Entity.Transform.Children.Count : Asset.Hierarchy.RootParts.Count;
        if (insertionIndex is { } requested && (requested < 0 || requested > targetCount)) throw new InvalidOperationException("Invalid hierarchy insertion index.");
        EndEdits();
        var name = sourcePath[(sourcePath.LastIndexOf('/') + 1)..];
        var replacement = targetPath.Length == 0 ? name : targetPath + "/" + name;
        var sourceFolders = GetHierarchyFolders(sourceOwner).ToArray();
        var movedPaths = sourceFolders.Where(path => InFolderBranch(path, sourcePath)).Select(path => replacement + path[sourcePath.Length..]).ToArray();
        var sourceAfter = sourceFolders.Where(path => !InFolderBranch(path, sourcePath)).ToArray();
        var changes = sourceOwner == targetOwner
            ? new[] { new FolderOwnerPathsChange(sourceOwner ?? Guid.Empty, sourceFolders, sourceAfter.Concat(movedPaths).ToArray()) }
            : new[] { new FolderOwnerPathsChange(sourceOwner ?? Guid.Empty, sourceFolders, sourceAfter),
                new FolderOwnerPathsChange(targetOwner ?? Guid.Empty, GetHierarchyFolders(targetOwner).ToArray(), GetHierarchyFolders(targetOwner).Concat(movedPaths).ToArray()) };
        var entities = HierarchyFolderEntities(sourceOwner, sourcePath);
        if (entities.Length > 0)
        {
            var folderMap = entities.ToDictionary(entity => entity.Id, entity => replacement + (Asset.Hierarchy.Parts[entity.Id].Folder ?? "")[sourcePath.Length..]);
            var index = insertionIndex ?? (targetOwner is { } target ? Asset.Hierarchy.Parts[target].Entity.Transform.Children.Count : Asset.Hierarchy.RootParts.Count);
            MoveEntitiesToFolder(entities, targetOwner, targetPath, keepWorld, index, foldersByEntity: folderMap, ownerFolders: changes);
        }
        else
        {
            var beforeRevision = revision; var afterRevision = ++nextRevision;
            void Apply(bool forward)
            {
                foreach (var change in changes) emptyHierarchyFolders[change.Owner] = new HashSet<string>(forward ? change.After : change.Before, StringComparer.OrdinalIgnoreCase);
                FolderVersion++; revision = forward ? afterRevision : beforeRevision;
            }
            try
            {
                Apply(true);
                using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(() => Apply(false), () => Apply(true)));
            }
            catch { Apply(false); throw; }
        }
        LastAction = "Move folder";
        return entities;
    }

    private static bool InFolderBranch(string? path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || (path ?? "").StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}
