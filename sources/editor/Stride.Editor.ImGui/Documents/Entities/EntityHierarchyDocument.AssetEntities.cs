using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanInsertAssetEntities(Guid? parentId, string folder, int index)
    {
        Entity? parent = null;
        if (parentId is { } id)
        {
            if (!Asset.Hierarchy.Parts.TryGetValue(id, out var part)) return false;
            parent = part.Entity;
        }
        return CanCreateEntity(parent) && (folder.Length == 0 || GetHierarchyFolders(parentId).Contains(folder, StringComparer.OrdinalIgnoreCase))
            && index >= -1 && index <= (parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count);
    }
    internal Entity[] InsertAssetEntities(Entity[] entities, Guid? parentId, string folder, int index,
        Vector3? position, params EditorAuthoringState[] references)
    {
        if (SessionHistory != null) return InsertAssetEntitiesWithResources(entities, parentId, folder, index, position, [], references);
        EndEdits(); EnsureHierarchyGraph();
        if (entities.Length == 0 || !CanInsertAssetEntities(parentId, folder, index))
            throw new InvalidOperationException("The assets cannot be added here.");
        if (entities.Any(entity => entity.Transform.Parent != null || entity.Transform.Children.Count != 0 || Asset.Hierarchy.Parts.ContainsKey(entity.Id))
            || entities.Select(entity => entity.Id).Distinct().Count() != entities.Length)
            throw new InvalidOperationException("Asset factories must create detached entities with unique identities.");
        if (position is { } point && (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)))
            throw new ArgumentException("The asset drop position must be finite.");
        var parent = parentId is { } owner ? Asset.Hierarchy.Parts[owner].Entity : null;
        if (index == -1) index = parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count;
        var collection = new AssetPartCollection<EntityDesign, Entity>();
        foreach (var entity in entities)
        {
            if (position is { } placement) entity.Transform.Position += placement;
            AssetCollectionItemIdHelper.GenerateMissingItemIds(entity); collection.Add(new EntityDesign(entity, folder));
        }
        var selection = SnapshotSelection(); var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Remove()
        {
            foreach (var entity in entities.Reverse())
                if (Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part)) hierarchyGraph!.RemovePartFromAsset(part);
            RestoreSelection(selection.Ids, selection.PrimaryId); revision = beforeRevision; InvalidateComponentStructure();
        }
        void Insert()
        {
            try
            {
                for (var offset = 0; offset < entities.Length; offset++)
                    hierarchyGraph!.AddPartToAsset(collection, collection[entities[offset].Id], parent, index + offset);
                RestoreSelection(entities.Select(entity => entity.Id), entities[^1].Id);
                revision = afterRevision; InvalidateComponentStructure();
            }
            catch (Exception failure)
            {
                try { Remove(); }
                catch (Exception recovery) { throw new AggregateException("Asset insertion and restoration failed.", failure, recovery); }
                throw;
            }
        }
        var applied = false; var recorded = false;
        try
        {
            using var transaction = CreateDocumentTransaction("Add assets to hierarchy");
            Insert(); applied = true;
            RecordOperation(new HierarchyCreationOperation(Remove, Insert), references); recorded = true;
        }
        catch { if (applied && !recorded) Remove(); throw; }
        return entities;
    }
    internal Entity[] InsertAssetEntitiesWithResources(Entity[] entities, Guid? parentId, string folder, int index,
        Vector3? position, IDisposable[] resources, params EditorAuthoringState[] references)
    {
        var retained = new List<IDisposable>(resources); var transferred = false;
        try
        {
            EndEdits(); EnsureHierarchyGraph();
            if (SessionHistory == null || entities.Length == 0 || !CanInsertAssetEntities(parentId, folder, index))
                throw new InvalidOperationException("The assets cannot be added here.");
            if (entities.Any(entity => entity.Transform.Parent != null || entity.Transform.Children.Count != 0 || Asset.Hierarchy.Parts.ContainsKey(entity.Id))
                || entities.Select(entity => entity.Id).Distinct().Count() != entities.Length)
                throw new InvalidOperationException("Asset factories must create detached entities with unique identities.");
            if (position is { } point && (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)))
                throw new ArgumentException("The asset drop position must be finite.");
            foreach (var reference in references) retained.Add(reference.AcquireHistory());
            var parent = parentId is { } owner ? Asset.Hierarchy.Parts[owner].Entity : null;
            if (index == -1) index = parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count;
            var collection = new AssetPartCollection<EntityDesign, Entity>();
            foreach (var entity in entities)
            {
                if (position is { } placement) entity.Transform.Position += placement;
                AssetCollectionItemIdHelper.GenerateMissingItemIds(entity); collection.Add(new EntityDesign(entity, folder));
            }
            transferred = true;
            ApplyInheritedGraphMutation(() =>
            {
                for (var offset = 0; offset < entities.Length; offset++)
                    HierarchyGraph.AddPartToAsset(collection, collection[entities[offset].Id], parent, index + offset);
                RestoreSelection(entities.Select(entity => entity.Id), entities[^1].Id);
            }, "Add assets to hierarchy", retained.ToArray());
            return entities;
        }
        finally { if (!transferred) foreach (var resource in retained) resource.Dispose(); }
    }
    internal void RecordImportedAssetSelection(IEnumerable<Guid> ids)
    {
        var before = SnapshotSelection(); var after = ids.Distinct().ToArray();
        using var transaction = CreateDocumentTransaction();
        RestoreSelection(after, after.LastOrDefault());
        RecordOperation(new HierarchyCreationOperation(() => RestoreSelection(before.Ids, before.PrimaryId),
            () => RestoreSelection(after, after.LastOrDefault())));
    }
}
