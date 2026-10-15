using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanReparent(Entity entity, Entity? parent)
    {
        return entity.Transform.Parent?.Entity != parent && CanMoveAtIndex(entity, parent, parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count);
    }
    internal bool CanMoveAtIndex(Entity entity, Entity? parent, int insertionIndex)
    {
        if (!CanDeleteEntity(entity) || !CanCreateEntity(parent)) return false;
        for (var ancestor = parent; ancestor != null; ancestor = ancestor.Transform.Parent?.Entity)
            if (ReferenceEquals(ancestor, entity)) return false;
        var count = parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count;
        if (insertionIndex < 0 || insertionIndex > count) return false;
        if (entity.Transform.Parent?.Entity == parent)
        {
            var oldIndex = parent?.Transform.Children.IndexOf(entity.Transform) ?? Asset.Hierarchy.RootParts.IndexOf(entity);
            if (insertionIndex == oldIndex || insertionIndex == oldIndex + 1) return false;
        }
        return true;
    }
    internal void Reparent(Entity entity, Entity? parent, bool keepWorld)
    {
        if (!CanReparent(entity, parent)) throw new InvalidOperationException("The hierarchy destination is invalid, unchanged or not editable.");
        MoveAtIndex(entity, parent, parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count, keepWorld);
    }
    internal void MoveAtIndex(Entity entity, Entity? parent, int insertionIndex, bool keepWorld)
    {
        if (!CanMoveAtIndex(entity, parent, insertionIndex)) throw new InvalidOperationException("Invalid, cyclic or unchanged hierarchy insertion.");
        EndEdits();
        if (RequiresHierarchyJournal && keepWorld && !CanAuthorHierarchyTransform(entity))
            throw new InvalidOperationException("The entity transform is sealed.");
        var before = TransformState.Read(entity); var after = before;
        if (keepWorld)
        {
            entity.Transform.UpdateWorldMatrix();
            entity.Transform.GetWorldTransformation(out var position, out var rotation, out var scale);
            if (parent != null)
            {
                parent.Transform.UpdateWorldMatrix();
                var determinant = parent.Transform.WorldMatrix.Determinant();
                if (!float.IsFinite(determinant) || Math.Abs(determinant) < 1e-12f)
                    throw new InvalidOperationException("The destination world transform is singular.");
                parent.Transform.WorldToLocal(ref position, ref rotation, ref scale);
            }
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
                || !float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z)
                || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
                throw new InvalidOperationException("The destination transform cannot represent a finite local transform.");
            after = new TransformState(position, rotation, scale);
        }
        EnsureHierarchyGraph();
        var oldParent = entity.Transform.Parent?.Entity;
        var oldIndex = oldParent?.Transform.Children.IndexOf(entity.Transform) ?? Asset.Hierarchy.RootParts.IndexOf(entity);
        var newIndex = insertionIndex;
        if (oldParent == parent && newIndex > oldIndex) newIndex--;
        var design = Asset.Hierarchy.Parts[entity.Id];
        var collection = new AssetPartCollection<EntityDesign, Entity>();
        foreach (var part in Subtree(entity)) collection.Add(Asset.Hierarchy.Parts[part.Id]);
        if (RequiresHierarchyJournal)
        {
            var sourceFolders = CapturePartRemovalFolders([entity]);
            var action = $"Move {entity.Name} to {parent?.Name ?? "scene root"}";
            RecordHierarchyFolderGroup(() =>
            {
                ApplyInheritedGraphMutation(() =>
                {
                    hierarchyGraph!.RemovePartFromAsset(design);
                    hierarchyGraph.AddPartToAsset(collection, design, parent, newIndex);
                    if (keepWorld) ApplyHierarchyTransform(entity, after);
                }, action);
                RetainPartRemovalFolders(sourceFolders, action);
            }, action);
            return;
        }
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Move(Entity? destination, int index, TransformState state, long destinationRevision)
        {
            var originParent = entity.Transform.Parent?.Entity;
            var originIndex = originParent?.Transform.Children.IndexOf(entity.Transform) ?? Asset.Hierarchy.RootParts.IndexOf(entity);
            hierarchyGraph!.RemovePartFromAsset(design);
            try { hierarchyGraph.AddPartToAsset(collection, design, destination, index); }
            catch
            {
                // Add failures must not leave the subtree detached from the asset.
                hierarchyGraph.AddPartToAsset(collection, design, originParent, originIndex);
                throw;
            }
            state.Apply(entity); revision = destinationRevision; StructureVersion++;
        }
        using (CreateDocumentTransaction())
        {
            Move(parent, newIndex, after, afterRevision);
            RecordOperation(new HierarchyCreationOperation(() => Move(oldParent, oldIndex, before, beforeRevision),
                () => Move(parent, newIndex, after, afterRevision)));
        }
        LastAction = $"Move {entity.Name} to {parent?.Name ?? "scene root"}";
    }
}
