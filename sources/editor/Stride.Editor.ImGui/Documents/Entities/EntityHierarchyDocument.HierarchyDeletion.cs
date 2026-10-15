using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool HasInheritedHierarchy { get; private set; }
    internal bool CanDeleteEntity(Entity entity) => CanAuthorHierarchyPart(entity);
    internal static IEnumerable<Entity> Subtree(Entity entity)
    {
        yield return entity;
        foreach (var child in entity.Transform.Children)
            foreach (var descendant in Subtree(child.Entity)) yield return descendant;
    }

    internal bool CanDeleteSelection => SelectedCount > 0 && SelectedIds.All(id =>
        Asset.Hierarchy.Parts.TryGetValue(id, out var part) && CanDeleteEntity(part.Entity));

    internal void DeleteSelectedEntities() => DeleteEntities(GetSelectedTransformRoots());
    internal void DeleteEntity(Entity entity) => DeleteEntities([entity]);

    internal void DeleteEntities(IEnumerable<Entity> entities)
    {
        var requested = entities.DistinctBy(entity => entity.Id).ToArray();
        if (requested.Length == 0 || requested.Any(entity => !CanDeleteEntity(entity)))
            throw new InvalidOperationException("The selected entities cannot be deleted.");
        var requestedIds = requested.Select(entity => entity.Id).ToHashSet();
        var roots = requested.Where(entity =>
        {
            for (var parent = entity.Transform.Parent; parent != null; parent = parent.Parent)
                if (requestedIds.Contains(parent.Entity.Id)) return false;
            return true;
        }).ToArray();
        EndEdits(); EnsureHierarchyGraph();
        if (RequiresHierarchyJournal)
        {
            var selection = SnapshotSelection();
            var designs = roots.Select(entity => Asset.Hierarchy.Parts[entity.Id]).ToArray();
            var folders = CapturePartRemovalFolders(roots);
            RecordHierarchyFolderGroup(() =>
            {
                ApplyInheritedGraphMutation(() =>
                {
                    hierarchyGraph!.DeleteParts(designs, out _);
                    RestoreSelection(selection.Ids, selection.PrimaryId);
                }, "Delete entities");
                RetainPartRemovalFolders(folders, "Delete entities");
            }, "Delete entities");
            return;
        }
        // Restore sibling roots in original collection order, independently of selection order.
        var orderedRoots = roots.OrderBy(entity => entity.Transform.Parent?.Children.IndexOf(entity.Transform)
            ?? Asset.Hierarchy.RootParts.IndexOf(entity)).ToArray();
        var parts = orderedRoots.SelectMany(Subtree).Select(part => new DeletedPart(Asset.Hierarchy.Parts[part.Id], part.Transform.Parent?.Entity,
            part.Transform.Parent?.Children.IndexOf(part.Transform) ?? Asset.Hierarchy.RootParts.IndexOf(part))).ToArray();
        var collection = new AssetPartCollection<EntityDesign, Entity>();
        foreach (var part in parts) collection.Add(part.Design);
        var references = new List<ClearedReference>();
        var previousSelection = SnapshotSelection();
        var rootDesigns = roots.Select(entity => Asset.Hierarchy.Parts[entity.Id]).ToArray();
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        void CaptureMember(object? sender, AssetMemberNodeChangeEventArgs change)
        {
            if (change.OldValue is Entity or EntityComponent && change.NewValue == null)
                references.Add(new ClearedReference(change.Member, NodeIndex.Empty, change.OldValue, change.PreviousOverride.HasFlag(OverrideType.New)));
        }
        void CaptureItem(object? sender, AssetItemNodeChangeEventArgs change)
        {
            if (change.ChangeType == ContentChangeType.CollectionUpdate && change.OldValue is Entity or EntityComponent && change.NewValue == null)
                references.Add(new ClearedReference(change.Collection, change.Index, change.OldValue, change.PreviousOverride.HasFlag(OverrideType.New)));
        }
        void Restore()
        {
            foreach (var part in parts)
                if (!Asset.Hierarchy.Parts.ContainsKey(part.Design.Entity.Id))
                    hierarchyGraph!.AddPartToAsset(collection, part.Design, part.Parent, part.Index);
            foreach (var reference in references.AsEnumerable().Reverse()) reference.Restore();
            RestoreSelection(previousSelection.Ids, previousSelection.PrimaryId); revision = beforeRevision; StructureVersion++;
        }
        void Delete()
        {
            try
            {
                hierarchyGraph!.DeleteParts(rootDesigns, out _);
                RestoreSelection(previousSelection.Ids, previousSelection.PrimaryId);
                revision = afterRevision; StructureVersion++;
            }
            catch (Exception error)
            {
                try { Restore(); }
                catch (Exception restoreError) { throw new AggregateException("Entity deletion and restoration failed.", error, restoreError); }
                throw;
            }
        }
        hierarchyGraph!.Changed += CaptureMember; hierarchyGraph.ItemChanged += CaptureItem;
        try { Delete(); }
        finally { hierarchyGraph.Changed -= CaptureMember; hierarchyGraph.ItemChanged -= CaptureItem; }
        try
        {
            using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(Restore, Delete));
        }
        catch { Restore(); throw; }
        LastAction = $"Delete {parts.Length} entit{(parts.Length == 1 ? "y" : "ies")}";
    }

    private sealed record DeletedPart(EntityDesign Design, Entity? Parent, int Index);
    private sealed record ClearedReference(IGraphNode Node, NodeIndex Index, object Value, bool WasOverridden)
    {
        internal void Restore()
        {
            if (Node is IAssetMemberNode member) { member.Update(Value); member.OverrideContent(WasOverridden); }
            else if (Node is IAssetObjectNode collection) { collection.Update(Value, Index); collection.OverrideItem(WasOverridden, Index); }
        }
    }
}
