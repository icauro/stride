using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanMoveEntitiesToFolder(IReadOnlyCollection<Entity> entities, Guid? owner)
    {
        var parent = owner is { } id && Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.Entity : null;
        if (owner != null && parent == null || entities.Count == 0 || !CanCreateEntity(parent) || entities.Any(entity => !CanDeleteEntity(entity)
            || !CanEditHierarchyNode(HierarchyGraph.Container.NodeContainer.GetOrCreateNode(Asset.Hierarchy.Parts[entity.Id])[nameof(EntityDesign.Folder)]))) return false;
        foreach (var entity in entities)
            for (var ancestor = parent; ancestor != null; ancestor = ancestor.Transform.Parent?.Entity)
                if (ancestor == entity) return false;
        return true;
    }

    internal void MoveEntitiesToFolder(IReadOnlyCollection<Entity> entities, Guid? owner, string path, bool keepWorld, int? insertionIndex = null,
        IReadOnlyDictionary<Guid, string>? foldersByEntity = null, FolderOwnerPathsChange[]? ownerFolders = null)
    {
        if (!CanMoveEntitiesToFolder(entities, owner) || path.Length > 0 && !GetHierarchyFolders(owner).Contains(path, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected entities cannot be moved to this folder.");
        EndEdits(); EnsureHierarchyGraph();
        var ids = entities.Select(entity => entity.Id).ToHashSet();
        var roots = entities.DistinctBy(entity => entity.Id).Where(entity =>
        {
            for (var ancestor = entity.Transform.Parent; ancestor != null; ancestor = ancestor.Parent)
                if (ids.Contains(ancestor.Entity.Id)) return false;
            return true;
        }).ToArray();
        if (ownerFolders == null && insertionIndex == null && roots.All(entity => entity.Transform.Parent?.Entity.Id == owner && Asset.Hierarchy.Parts[entity.Id].Folder == path)) return;
        var destination = owner is { } targetId ? Asset.Hierarchy.Parts[targetId].Entity : null;
        var before = roots.Select(entity => ReadFolderPlacement(entity)).ToArray();
        var destinationCount = destination?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count;
        if (insertionIndex is { } requestedIndex && (requestedIndex < 0 || requestedIndex > destinationCount)) throw new InvalidOperationException("Invalid hierarchy insertion index.");
        var nextIndex = insertionIndex ?? destinationCount;
        if (insertionIndex != null) nextIndex -= before.Count(placement => placement.Parent == destination && placement.Index < insertionIndex.Value);
        var after = roots.Select(entity => new FolderPlacement(entity, destination,
            insertionIndex == null && entity.Transform.Parent?.Entity == destination ? ReadFolderPlacement(entity).Index : nextIndex++, foldersByEntity?.GetValueOrDefault(entity.Id) ?? path,
            ReadFolderMoveTransform(entity, destination, keepWorld))).ToArray();
        var collection = new AssetPartCollection<EntityDesign, Entity>();
        foreach (var entity in roots.SelectMany(Subtree)) collection.Add(Asset.Hierarchy.Parts[entity.Id]);
        if (RequiresHierarchyJournal)
        {
            foreach (var placement in after)
            {
                if (keepWorld && !CanAuthorHierarchyTransform(placement.Entity)) throw new InvalidOperationException("The entity transform is sealed.");
                var designNode = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(collection[placement.Entity.Id]);
                if (!CanEditHierarchyNode(designNode[nameof(EntityDesign.Folder)])) throw new InvalidOperationException("This hierarchy folder is sealed.");
            }
            var sourceFolders = CapturePartRemovalFolders(roots);
            RecordHierarchyFolderGroup(() =>
            {
                ApplyInheritedGraphMutation(() =>
                {
                    foreach (var root in roots) hierarchyGraph!.RemovePartFromAsset(collection[root.Id]);
                    foreach (var placement in after.OrderBy(placement => placement.Index))
                    {
                        var design = collection[placement.Entity.Id];
                        hierarchyGraph!.AddPartToAsset(collection, design, placement.Parent, placement.Index);
                        hierarchyGraph.Container.NodeContainer.GetOrCreateNode(design)[nameof(EntityDesign.Folder)].Update(placement.Folder);
                        if (keepWorld) ApplyHierarchyTransform(placement.Entity, placement.Transform);
                    }
                }, "Move entities to folder");
                RetainPartRemovalFolders(sourceFolders, "Move entities to folder");
                if (ownerFolders != null) ChangeHierarchyFolderOwnerPaths(ownerFolders, "Move folder");
            }, "Move entities to folder");
            return;
        }
        var beforeRevision = revision; var afterRevision = ++nextRevision; var selection = SnapshotSelection();
        void ApplyCore(FolderPlacement[] placements, long nextRevisionValue, bool forward)
        {
            foreach (var entity in roots)
                if (Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design)) hierarchyGraph!.RemovePartFromAsset(design);
            foreach (var placement in placements.OrderBy(placement => placement.Index))
            {
                var design = collection[placement.Entity.Id];
                hierarchyGraph!.AddPartToAsset(collection, design, placement.Parent, placement.Index);
                hierarchyGraph.Container.NodeContainer.GetOrCreateNode(design)[nameof(EntityDesign.Folder)].Update(placement.Folder);
                placement.Transform.Apply(placement.Entity);
            }
            if (ownerFolders != null)
                foreach (var change in ownerFolders)
                    emptyHierarchyFolders[change.Owner] = new HashSet<string>(forward ? change.After : change.Before, StringComparer.OrdinalIgnoreCase);
            revision = nextRevisionValue; FolderVersion++; StructureVersion++;
            RestoreSelection(selection.Ids, selection.PrimaryId);
        }
        void Apply(FolderPlacement[] placements, long nextRevisionValue, bool forward)
        {
            var previous = roots.Select(ReadFolderPlacement).ToArray(); var previousRevision = revision;
            try { ApplyCore(placements, nextRevisionValue, forward); }
            catch (Exception error)
            {
                try { ApplyCore(previous, previousRevision, !forward); }
                catch (Exception restoreError) { throw new AggregateException("Hierarchy folder move and restoration failed.", error, restoreError); }
                throw;
            }
        }
        Apply(after, afterRevision, true);
        try
        {
            using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(
                () => Apply(before, beforeRevision, false), () => Apply(after, afterRevision, true)));
        }
        catch { Apply(before, beforeRevision, false); throw; }
        LastAction = "Move entities to folder";
    }

    private FolderPlacement ReadFolderPlacement(Entity entity) => new(entity, entity.Transform.Parent?.Entity,
        entity.Transform.Parent?.Children.IndexOf(entity.Transform) ?? Asset.Hierarchy.RootParts.IndexOf(entity),
        Asset.Hierarchy.Parts[entity.Id].Folder, TransformState.Read(entity));

    private static TransformState ReadFolderMoveTransform(Entity entity, Entity? parent, bool keepWorld)
    {
        if (!keepWorld || entity.Transform.Parent?.Entity == parent) return TransformState.Read(entity);
        entity.Transform.UpdateWorldMatrix(); entity.Transform.GetWorldTransformation(out var position, out var rotation, out var scale);
        if (parent != null)
        {
            parent.Transform.UpdateWorldMatrix(); var determinant = parent.Transform.WorldMatrix.Determinant();
            if (!float.IsFinite(determinant) || Math.Abs(determinant) < 1e-12f) throw new InvalidOperationException("The destination world transform is singular.");
            parent.Transform.WorldToLocal(ref position, ref rotation, ref scale);
        }
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(scale.X) || !float.IsFinite(scale.Y)
            || !float.IsFinite(scale.Z) || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
            throw new InvalidOperationException("The destination transform cannot represent a finite local transform.");
        return new(position, rotation, scale);
    }
    private sealed record FolderPlacement(Entity Entity, Entity? Parent, int Index, string Folder, TransformState Transform);
}
