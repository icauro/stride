using Stride.Assets.Entities;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanMoveHierarchyFolderToScene(EntityHierarchyDocument destination, Guid? sourceOwner, string sourcePath,
        Guid? targetOwner, string targetPath)
    {
        if (ReferenceEquals(this, destination)
            || SessionHistory == null || !ReferenceEquals(SessionHistory, destination.SessionHistory)
            || !CanEditHierarchyFolders(sourceOwner) || !destination.CanEditHierarchyFolders(targetOwner)
            || !GetHierarchyFolders(sourceOwner).Contains(sourcePath, StringComparer.OrdinalIgnoreCase)
            || targetPath.Length > 0 && !destination.GetHierarchyFolders(targetOwner).Contains(targetPath, StringComparer.OrdinalIgnoreCase)) return false;
        var name = sourcePath[(sourcePath.LastIndexOf('/') + 1)..];
        var replacement = targetPath.Length == 0 ? name : targetPath + "/" + name;
        var roots = HierarchyFolderEntities(sourceOwner, sourcePath);
        return !destination.GetHierarchyFolders(targetOwner).Contains(replacement, StringComparer.OrdinalIgnoreCase)
            && (roots.Length == 0 || CanMoveHierarchyRootsTo(destination, roots));
    }

    internal IReadOnlyList<Stride.Engine.Entity> MoveHierarchyFolderToScene(EntityHierarchyDocument destination, Guid? sourceOwner, string sourcePath,
        Guid? targetOwner, string targetPath, bool keepWorld, Vector3 sourceWorldOffset, Vector3 destinationWorldOffset, int index = -1)
    {
        if (!CanMoveHierarchyFolderToScene(destination, sourceOwner, sourcePath, targetOwner, targetPath))
            throw new InvalidOperationException("This hierarchy folder cannot be moved here.");
        var targetCount = targetOwner is { } targetId ? destination.Asset.Hierarchy.Parts[targetId].Entity.Transform.Children.Count : destination.Asset.Hierarchy.RootParts.Count;
        if (index < -1 || index > targetCount) throw new InvalidOperationException("Invalid hierarchy insertion index.");
        EndEdits(); destination.EndEdits();
        var sourceState = SessionHistory!.Registry.Find(Asset.Id); var targetState = SessionHistory.Registry.Find(destination.Asset.Id);
        if (!ReferenceEquals(sourceState?.Document, this) || !ReferenceEquals(targetState?.Document, destination))
            throw new InvalidOperationException("The scene authored generation changed.");
        static Dictionary<Guid, HashSet<string>> Snapshot(EntityHierarchyDocument document) => document.emptyHierarchyFolders
            .ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase));
        var beforeSource = Snapshot(this); var beforeTarget = Snapshot(destination);
        var afterSource = Snapshot(this); var afterTarget = Snapshot(destination);
        var name = sourcePath[(sourcePath.LastIndexOf('/') + 1)..];
        var replacement = targetPath.Length == 0 ? name : targetPath + "/" + name;
        var paths = GetHierarchyFolders(sourceOwner);
        afterSource[sourceOwner ?? Guid.Empty] = new(paths.Where(path => !InFolderBranch(path, sourcePath)), StringComparer.OrdinalIgnoreCase);
        afterTarget[targetOwner ?? Guid.Empty] = new(destination.GetHierarchyFolders(targetOwner)
            .Concat(paths.Where(path => InFolderBranch(path, sourcePath)).Select(path => replacement + path[sourcePath.Length..])), StringComparer.OrdinalIgnoreCase);
        var roots = HierarchyChildren(sourceOwner).Where(entity => InFolderBranch(Asset.Hierarchy.Parts[entity.Id].Folder, sourcePath)).ToArray();
        var map = roots.ToDictionary(entity => entity.Id, entity => replacement + (Asset.Hierarchy.Parts[entity.Id].Folder ?? "")[sourcePath.Length..]);
        var transfer = roots.Length == 0 ? null : PrepareMoveToScene(destination, targetOwner, targetPath, keepWorld,
            sourceWorldOffset, destinationWorldOffset, index, explicitRoots: roots, foldersByEntity: map);
        foreach (var entity in roots.SelectMany(Subtree))
        {
            afterSource.Remove(entity.Id);
            var folders = GetHierarchyFolders(entity.Id);
            if (folders.Count == 0) continue;
            var owner = transfer!.IdentityRemapping.GetValueOrDefault(entity.Id, entity.Id);
            if (afterTarget.ContainsKey(owner)) throw new InvalidOperationException("The destination folder owner identity is already present.");
            afterTarget.Add(owner, new(folders, StringComparer.OrdinalIgnoreCase));
        }
        var sourceBefore = revision; var targetBefore = destination.revision;
        var sourceAfter = transfer == null ? ++nextRevision : sourceBefore;
        var targetAfter = transfer == null ? ++destination.nextRevision : targetBefore;
        void Metadata(bool forward)
        {
            var sourceFolders = (forward ? afterSource : beforeSource).ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase));
            var targetFolders = (forward ? afterTarget : beforeTarget).ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase));
            emptyHierarchyFolders = sourceFolders; destination.emptyHierarchyFolders = targetFolders;
            FolderVersion++; destination.FolderVersion++;
            if (transfer == null) { revision = forward ? sourceAfter : sourceBefore; destination.revision = forward ? targetAfter : targetBefore; }
        }
        var metadata = new HierarchyCreationOperation(() => Metadata(false), () => Metadata(true));
        void Apply()
        {
            transfer?.ApplyWithJournals();
            try
            {
                Metadata(true);
                RecordOperation(metadata, sourceState!, targetState!);
            }
            catch { Metadata(false); throw; }
        }
        using var transaction = CreateDocumentTransaction(HierarchyTransferOperationName);
        if (SessionHistory.IsCapturingOperations) Apply();
        else { using var capture = SessionHistory.CaptureOperationsAtomic(Apply); capture.Publish(SessionHistory, HierarchyTransferOperationName); }
        LastAction = destination.LastAction = HierarchyTransferOperationName;
        return transfer?.MovedRoots ?? [];
    }
}
