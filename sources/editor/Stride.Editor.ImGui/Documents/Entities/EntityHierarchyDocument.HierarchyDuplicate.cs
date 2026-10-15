using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanDuplicateEntitySubset(Entity[] roots) => CanSave && SessionHistory != null
        && roots.Length > 0 && roots.All(entity => IsSelected(entity.Id))
        && CanCopyHierarchyRoots(roots) && roots.All(root => CanCreateEntity(root.Transform.Parent?.Entity));

    internal void DuplicateEntitySubset(Entity[] roots, Guid[] preservedSelection)
    {
        if (!CanDuplicateEntitySubset(roots)) throw new InvalidOperationException("The selected entities cannot be duplicated.");
        DuplicateEntitiesWithJournal(roots, preservedSelection);
    }

    internal void DuplicateSelectedEntities()
    {
        if (!CanSave || !CanCopyEntities) throw new InvalidOperationException("The selected entities cannot be duplicated.");
        if (SessionHistory != null)
        {
            DuplicateEntitiesWithJournal();
            return;
        }
        EndEdits(); EnsureHierarchyGraph();
        var roots = GetSelectedTransformRoots(); var selection = SnapshotSelection();
        var copies = new List<(AssetCompositeHierarchyData<EntityDesign, Entity> Hierarchy, DeletedPart[] Parts)>();
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Remove()
        {
            foreach (var copy in copies.AsEnumerable().Reverse())
                foreach (var part in copy.Parts.Reverse())
                    if (Asset.Hierarchy.Parts.TryGetValue(part.Design.Entity.Id, out var present)) hierarchyGraph!.RemovePartFromAsset(present);
            RestoreSelection(selection.Ids, selection.PrimaryId); revision = beforeRevision; StructureVersion++;
        }
        void Insert()
        {
            foreach (var copy in copies)
                foreach (var part in copy.Parts)
                    if (!Asset.Hierarchy.Parts.ContainsKey(part.Design.Entity.Id)) hierarchyGraph!.AddPartToAsset(copy.Hierarchy.Parts, part.Design, part.Parent, part.Index);
            RestoreSelection(copies.Select(copy => copy.Parts[0].Design.Entity.Id), copies[^1].Parts[0].Design.Entity.Id);
            revision = afterRevision; StructureVersion++;
        }
        try
        {
            foreach (var source in roots)
            {
                var hierarchy = AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>.CloneSubHierarchies(
                    hierarchyGraph!.Container.NodeContainer, Asset, [source.Id], SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects, out _);
                var root = hierarchy.RootParts.Single(); var parent = source.Transform.Parent?.Entity;
                // WPF EntityFactory.ComputeNewName currently preserves the original name.
                root.Name = source.Name;
                var index = (parent?.Transform.Children.IndexOf(source.Transform) ?? Asset.Hierarchy.RootParts.IndexOf(source)) + 1;
                var parts = Subtree(root).Select(entity => new DeletedPart(hierarchy.Parts[entity.Id], entity == root ? parent : entity.Transform.Parent?.Entity,
                    entity == root ? index : entity.Transform.Parent!.Children.IndexOf(entity.Transform))).ToArray();
                copies.Add((hierarchy, parts));
                hierarchyGraph.AddPartToAsset(hierarchy.Parts, hierarchy.Parts[root.Id], parent, index);
            }
            RestoreSelection(copies.Select(copy => copy.Parts[0].Design.Entity.Id), copies[^1].Parts[0].Design.Entity.Id);
            revision = afterRevision; StructureVersion++;
            using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(Remove, Insert));
        }
        catch (Exception error)
        {
            try { Remove(); }
            catch (Exception restoreError) { throw new AggregateException("Entity duplication and restoration failed.", error, restoreError); }
            throw;
        }
        LastAction = "Duplicate entities";
    }
}
