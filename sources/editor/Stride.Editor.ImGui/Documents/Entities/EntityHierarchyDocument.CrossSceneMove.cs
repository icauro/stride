using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal PreparedCrossSceneMove PrepareMoveToScene(EntityHierarchyDocument destination, Guid? parentId, string folder, bool keepWorld,
        Vector3? sourceWorldOffset = null, Vector3? destinationWorldOffset = null, int index = -1,
        IReadOnlyCollection<Entity>? explicitRoots = null, IReadOnlyDictionary<Guid, string>? foldersByEntity = null)
    {
        if (!CanSave || !destination.CanSave || destination == this
            || (explicitRoots == null ? !CanCopyEntities || !CanDeleteSelection
                : explicitRoots.Count == 0 || HasUnloadableObjects || explicitRoots.Any(entity => !CanDeleteEntity(entity)))
            || SessionHistory == null || !ReferenceEquals(SessionHistory, destination.SessionHistory)
            || !destination.CanEditHierarchyFolders(parentId)
            || folder.Length > 0 && !destination.GetHierarchyFolders(parentId).Contains(folder, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected hierarchy cannot be moved here.");
        var sourceScene = Asset as SceneAsset;
        var destinationScene = destination.Asset as SceneAsset;
        if (keepWorld && (sourceScene?.Parent != null || destinationScene?.Parent != null) && (sourceWorldOffset == null || destinationWorldOffset == null))
            throw new InvalidOperationException("Moving between nested scenes requires the resolved scene hierarchy.");
        EndEdits(); destination.EndEdits(); EnsureHierarchyGraph(); destination.EnsureHierarchyGraph();
        if (!CanMoveHierarchyRootsTo(destination, explicitRoots ?? GetSelectedTransformRoots()))
            throw new InvalidOperationException("The selected hierarchy depends on its destination or cannot be moved.");
        var count = parentId is { } parent ? destination.Asset.Hierarchy.Parts[parent].Entity.Transform.Children.Count : destination.Asset.Hierarchy.RootParts.Count;
        if (index < -1 || index > count) throw new InvalidOperationException("The destination hierarchy index changed.");
        return new PreparedCrossSceneMove(this, destination, parentId, folder, keepWorld,
            sourceWorldOffset ?? sourceScene?.Offset ?? Vector3.Zero, destinationWorldOffset ?? destinationScene?.Offset ?? Vector3.Zero,
            index == -1 ? count : index, explicitRoots, foldersByEntity);
    }

    internal void ApplyPreparedSceneMove(PreparedCrossSceneMove transfer)
    {
        if (!ReferenceEquals(transfer.Source, this) || SessionHistory == null || !ReferenceEquals(SessionHistory, transfer.Destination.SessionHistory))
            throw new InvalidOperationException("Scene transfers require the canonical shared session history.");
        var sourceOwner = SessionHistory.Registry.Find(Asset.Id);
        var destinationOwner = SessionHistory.Registry.Find(transfer.Destination.Asset.Id);
        if (!ReferenceEquals(sourceOwner?.Document, this) || !ReferenceEquals(destinationOwner?.Document, transfer.Destination))
            throw new InvalidOperationException("The scene authored generation changed.");
        void ApplyAndRecord()
        {
            transfer.ApplyWithJournals();
        }
        using var transaction = CreateDocumentTransaction(HierarchyTransferOperationName);
        if (SessionHistory.IsCapturingOperations) ApplyAndRecord();
        else
        {
            using var captured = SessionHistory.CaptureOperationsAtomic(ApplyAndRecord);
            captured.Publish(SessionHistory, HierarchyTransferOperationName);
        }
    }

    internal sealed class PreparedCrossSceneMove
    {
        internal EntityHierarchyDocument Source { get; }
        internal EntityHierarchyDocument Destination { get; }
        internal IReadOnlyDictionary<Guid, Guid> IdentityRemapping { get; }
        internal IReadOnlyList<Entity> MovedRoots => clonedRoots;
        private readonly AssetCompositeHierarchyData<EntityDesign, Entity> copied;
        private readonly DeletedPart[] originals;
        private readonly Entity[] clonedRoots;
        private readonly Entity? destinationParent;
        private readonly int destinationIndex;
        private readonly EntitySelectionSnapshot sourceSelection;
        private readonly long sourceBefore, destinationBefore;
        private readonly long sourceStructure, destinationStructure;
        private readonly (string Folder, TransformState Transform)[] placements;
        private bool initialized;

        internal PreparedCrossSceneMove(EntityHierarchyDocument source, EntityHierarchyDocument destination, Guid? parentId, string folder, bool keepWorld,
            Vector3 sourceWorldOffset, Vector3 destinationWorldOffset, int destinationInsertionIndex, IReadOnlyCollection<Entity>? explicitRoots, IReadOnlyDictionary<Guid, string>? foldersByEntity)
        {
            Source = source; Destination = destination;
            var roots = explicitRoots?.ToArray() ?? source.GetSelectedTransformRoots();
            var selected = roots.Select(entity => entity.Id).ToHashSet();
            if (roots.Select(entity => entity.Id).Distinct().Count() != roots.Length || roots.Any(entity =>
                !source.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)))
                throw new InvalidOperationException("The scene transfer roots changed.");
            roots = roots.Where(entity =>
            {
                for (var parent = entity.Transform.Parent; parent != null; parent = parent.Parent) if (selected.Contains(parent.Entity.Id)) return false;
                return true;
            }).ToArray();
            originals = roots.Select(entity => new DeletedPart(source.Asset.Hierarchy.Parts[entity.Id], entity.Transform.Parent?.Entity,
                entity.Transform.Parent?.Children.IndexOf(entity.Transform) ?? source.Asset.Hierarchy.RootParts.IndexOf(entity))).ToArray();
            copied = AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>.CloneSubHierarchies(source.hierarchyGraph!.Container.NodeContainer,
                destination.hierarchyGraph!.Container.NodeContainer, source.Asset, roots.Select(entity => entity.Id), SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects, out var remapping);
            IdentityRemapping = remapping;
            clonedRoots = copied.RootParts.ToArray();
            if (copied.Parts.Keys.Any(destination.Asset.Hierarchy.Parts.ContainsKey))
                throw new InvalidOperationException("The scene transfer would duplicate an entity identity.");
            destinationParent = parentId is { } id ? destination.Asset.Hierarchy.Parts[id].Entity : null;
            destinationIndex = destinationInsertionIndex;
            placements = new (string, TransformState)[roots.Length];
            for (var index = 0; index < roots.Length; index++)
            {
                var destinationFolder = foldersByEntity?.GetValueOrDefault(roots[index].Id) ?? folder;
                var state = TransformState.Read(roots[index]);
                if (keepWorld)
                {
                    roots[index].Transform.UpdateWorldMatrix();
                    roots[index].Transform.GetWorldTransformation(out var position, out var rotation, out var scale);
                    // Asset-side roots are detached from Scene instances; account for scene offsets explicitly.
                    position += sourceWorldOffset - destinationWorldOffset;
                    if (destinationParent != null)
                    {
                        destinationParent.Transform.UpdateWorldMatrix();
                        var determinant = destinationParent.Transform.WorldMatrix.Determinant();
                        if (!float.IsFinite(determinant) || Math.Abs(determinant) < 1e-12f) throw new InvalidOperationException("The destination world transform is singular.");
                        destinationParent.Transform.WorldToLocal(ref position, ref rotation, ref scale);
                    }
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(scale.X) || !float.IsFinite(scale.Y)
                        || !float.IsFinite(scale.Z) || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
                        throw new InvalidOperationException("The destination transform cannot represent a finite local transform.");
                    state = new(position, rotation, scale);
                }
                placements[index] = (destinationFolder, state);
            }
            sourceSelection = source.SnapshotSelection();
            sourceBefore = source.revision;
            destinationBefore = destination.revision;
            sourceStructure = source.StructureVersion; destinationStructure = destination.StructureVersion;
        }

        internal void ApplyWithJournals()
        {
            var history = Source.SessionHistory;
            if (history == null || !ReferenceEquals(history, Destination.SessionHistory) || !history.IsCapturingOperations
                || !ReferenceEquals(history.Registry.Find(Source.Asset.Id)?.Document, Source)
                || !ReferenceEquals(history.Registry.Find(Destination.Asset.Id)?.Document, Destination))
                throw new InvalidOperationException("Scene transfers require a canonical shared history capture.");
            if (initialized || Source.revision != sourceBefore || Destination.revision != destinationBefore
                || Source.StructureVersion != sourceStructure || Destination.StructureVersion != destinationStructure)
                throw new InvalidOperationException("The scene transfer plan is no longer current.");
            if (originals.Any(part => !Source.CanDeleteEntity(part.Design.Entity)) || !Destination.CanCreateEntity(destinationParent))
                throw new InvalidOperationException("The scene transfer topology is sealed.");
            Destination.PrepareClipboardBases([copied]);
            var sourceFolders = Source.CapturePartRemovalFolders(originals.Select(part => part.Design.Entity));
            try
            {
                Source.ApplyInheritedGraphMutation(() =>
                {
                    foreach (var part in originals) Source.HierarchyGraph.RemovePartFromAsset(part.Design);
                    FixExternalReferences(Source);
                    Source.RestoreSelection(sourceSelection.Ids, sourceSelection.PrimaryId);
                }, HierarchyTransferOperationName);
                Source.RetainPartRemovalFolders(sourceFolders, HierarchyTransferOperationName);
                Destination.ApplyInheritedGraphMutation(() =>
                {
                    for (var index = 0; index < clonedRoots.Length; index++)
                    {
                        var design = copied.Parts[clonedRoots[index].Id];
                        Destination.HierarchyGraph.AddPartToAsset(copied.Parts, design, destinationParent, destinationIndex + index);
                        var folderNode = Destination.HierarchyGraph.Container.NodeContainer.GetOrCreateNode(design)[nameof(EntityDesign.Folder)];
                        if (!Destination.CanEditHierarchyNode(folderNode)) throw new InvalidOperationException("The moved entity folder is sealed.");
                        folderNode.Update(placements[index].Folder);
                        if (TransformState.Read(design.Entity) != placements[index].Transform)
                        {
                            if (!Destination.CanAuthorHierarchyTransform(design.Entity)) throw new InvalidOperationException("The moved entity transform is sealed.");
                            Destination.ApplyHierarchyTransform(design.Entity, placements[index].Transform);
                        }
                    }
                    FixExternalReferences(Destination);
                    Destination.RestoreSelection(clonedRoots.Select(entity => entity.Id), clonedRoots[^1].Id);
                }, HierarchyTransferOperationName);
                initialized = true;
            }
            catch (Exception failure)
            {
                Destination.RefreshClipboardBasesAfterFailure(failure);
                throw;
            }
        }

        private static void FixExternalReferences(EntityHierarchyDocument document)
        {
            var graph = document.HierarchyGraph;
            var available = IdentifiableObjectCollector.Collect(graph.Definition, graph.RootNode);
            var external = ExternalReferenceCollector.GetExternalReferences(graph.Definition, graph.RootNode).Select(value => value.Id).ToHashSet();
            var visitor = new ObjectReferencePathGenerator(graph.Definition) { ShouldOutputReference = external.Contains };
            visitor.Visit(graph.RootNode);
            var changes = new List<Action>();
            Stride.Core.Assets.Serializers.FixupObjectReferences.FixupReferences(document.Asset, visitor.Result, available, true, (path, _, value) =>
            {
                var graphPath = GraphNodePath.From(graph.RootNode, path, out var index); var node = graphPath.GetNode();
                if (!document.CanEditHierarchyNode(node)) throw new InvalidOperationException("A scene transfer reference is sealed.");
                // Native updates author the new local override; the owning graph journal
                // captures complete before/after member, item, key and deleted-ID metadata.
                changes.Add(() =>
                {
                    if (node is IMemberNode member) member.Update(value);
                    else ((IObjectNode)node).Update(value, index);
                });
            });
            foreach (var change in changes) change();
        }
    }
}
