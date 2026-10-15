using Stride.Assets.Entities;
using Stride.Assets.Presentation.ViewModel.CopyPasteProcessors;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Assets.Serializers;
using Stride.Core.Yaml;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private CopyPasteService? entityClipboardService;
    internal bool CanCopyEntities => SelectedCount > 0 && CanCopyHierarchyRoots(GetSelectedTransformRoots());
    private CopyPasteService EntityClipboardService
    {
        get
        {
            EnsureHierarchyGraph();
            if (entityClipboardService != null) return entityClipboardService;
            var settings = AssetYamlSerializer.Default.GetSerializerSettings();
            settings.RegisterTagMapping("!CopyPasteData", typeof(CopyPasteData));
            settings.RegisterTagMapping("!CopyPasteItem", typeof(CopyPasteItem));
            entityClipboardService = new CopyPasteService(hierarchyGraph!.Container);
            entityClipboardService.RegisterProcessor(new EntityHierarchyPasteProcessor());
            return entityClipboardService;
        }
    }

    internal string CopySelectedEntities()
    {
        if (!CanCopyEntities) throw new InvalidOperationException("The selected entities cannot be copied.");
        EndEdits(); EnsureHierarchyGraph();
        if (!CanCopyEntities) throw new InvalidOperationException("The selected clipboard closure changed.");
        var hierarchy = AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>.CloneSubHierarchies(
            hierarchyGraph!.Container.NodeContainer, Asset, GetSelectedTransformRoots().Select(entity => entity.Id), SubHierarchyCloneFlags.None, out _);
        foreach (var part in hierarchy.Parts.Values) part.Folder = null;
        return EntityClipboardService.CopyFromAsset(hierarchyGraph, Asset.Id, hierarchy, false);
    }

    internal static string CopySelectedEntities(IReadOnlyList<EntityHierarchyDocument> owners,
        IReadOnlyList<(EntityHierarchyDocument Document, Guid? Owner, string Path)>? copiedFolders = null)
    {
        foreach (var owner in owners) owner.EndEdits();
        if (owners.Count == 0 || owners.Any(owner => !owner.CanCopyEntities && (owner.SelectedCount != 0 || copiedFolders == null
            || !copiedFolders.Any(folder => ReferenceEquals(folder.Document, owner) && owner.CanCopyHierarchyFolder(folder.Owner, folder.Path))))
            || copiedFolders?.Any(folder => !folder.Document.CanCopyHierarchyFolder(folder.Owner, folder.Path)) == true)
            throw new InvalidOperationException("The selected entities cannot be copied.");
        var session = owners[0].SessionHistory;
        if (owners.Count > 1 && (session == null || owners.Any(owner => !ReferenceEquals(owner.SessionHistory, session))))
            throw new InvalidOperationException("Copying entities across scenes requires the canonical shared session.");
        var items = new List<(AssetPropertyGraph propertyGraph, AssetId? sourceId, object value, bool isObjectReference)>();
        foreach (var owner in owners)
        {
            owner.EndEdits(); owner.EnsureHierarchyGraph();
            var hierarchy = AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>.CloneSubHierarchies(
                owner.hierarchyGraph!.Container.NodeContainer, owner.Asset, owner.GetSelectedTransformRoots().Select(entity => entity.Id), SubHierarchyCloneFlags.None, out _);
            foreach (var part in hierarchy.Parts.Values) part.Folder = null;
            if (copiedFolders != null)
            {
                foreach (var folder in copiedFolders.Where(folder => ReferenceEquals(folder.Document, owner)))
                {
                    if (copiedFolders.Any(other => ReferenceEquals(other.Document, owner) && other.Owner == folder.Owner
                        && !string.Equals(other.Path, folder.Path, StringComparison.OrdinalIgnoreCase)
                        && folder.Path.StartsWith(other.Path + "/", StringComparison.OrdinalIgnoreCase))) continue;
                    var rootIds = hierarchy.RootParts.Select(root => root.Id).ToHashSet();
                    var ancestor = folder.Owner is { } parent && owner.Asset.Hierarchy.Parts.TryGetValue(parent, out var parentPart) ? parentPart.Entity.Transform : null;
                    var withinCopiedEntity = false;
                    for (; ancestor != null; ancestor = ancestor.Parent)
                        if (rootIds.Contains(ancestor.Entity.Id)) { withinCopiedEntity = true; break; }
                    if (withinCopiedEntity) continue;
                    var prefixLength = folder.Path.LastIndexOf('/') + 1;
                    foreach (var entity in owner.HierarchyChildren(folder.Owner))
                    {
                        var path = owner.Asset.Hierarchy.Parts[entity.Id].Folder ?? "";
                        if (InFolderBranch(path, folder.Path) && hierarchy.Parts.TryGetValue(entity.Id, out var design))
                            design.Folder = path[prefixLength..];
                    }
                }
            }
            items.Add((owner.hierarchyGraph, owner.Asset.Id, hierarchy, false));
        }
        var text = owners[0].EntityClipboardService.CopyFromAssets(items, typeof(AssetCompositeHierarchyData<EntityDesign, Entity>));
        return copiedFolders == null ? text : HierarchyFolderClipboardMetadata.Attach(text, copiedFolders);
    }

    internal bool CanPasteEntities(string text, bool asRoot)
    {
        if (!CanSave || SessionHistory == null && (HasInheritedHierarchy || Asset.Archetype != null)
            || string.IsNullOrWhiteSpace(text) || text.Length > 16 * 1024 * 1024 || asRoot && !CanCreateEntity(null)) return false;
        if (!asRoot && (SelectedCount == 0 || SelectedIds.Any(id => !Asset.Hierarchy.Parts.TryGetValue(id, out var part) || !CanCreateEntity(part.Entity)))) return false;
        return EntityClipboardService.CanPaste(text, Asset.GetType(), asRoot ? typeof(AssetCompositeHierarchyData<EntityDesign, Entity>) : typeof(Entity),
            typeof(AssetCompositeHierarchyData<EntityDesign, Entity>));
    }

    internal void PasteEntities(string text, bool asRoot)
    {
        if (!CanPasteEntities(text, asRoot)) throw new InvalidOperationException("The clipboard does not contain a compatible entity hierarchy.");
        if (SessionHistory != null)
        {
            PasteEntitiesWithJournal(text, asRoot ? new Entity?[] { null } : SelectedIds.Select(id => (Entity?)Asset.Hierarchy.Parts[id].Entity).ToArray());
            return;
        }
        EndEdits();
        var parents = asRoot ? new Entity?[] { null } : SelectedIds.Select(id => (Entity?)Asset.Hierarchy.Parts[id].Entity).ToArray();
        var beforeSelection = SnapshotSelection(); var beforeRevision = revision; var afterRevision = ++nextRevision;
        var inserted = new List<(AssetCompositeHierarchyData<EntityDesign, Entity> Hierarchy, Entity Root, DeletedPart[] Parts)>();
        var references = new List<HierarchyClipboardReference>();
        void Insert()
        {
            foreach (var entry in inserted)
                foreach (var part in entry.Parts)
                    if (!Asset.Hierarchy.Parts.ContainsKey(part.Design.Entity.Id))
                        hierarchyGraph!.AddPartToAsset(entry.Hierarchy.Parts, part.Design, part.Parent, part.Index);
            foreach (var reference in references) reference.Apply(true);
            RestoreSelection(inserted.Select(entry => entry.Root.Id), inserted.LastOrDefault().Root?.Id);
            revision = afterRevision; StructureVersion++;
        }
        void Remove()
        {
            foreach (var reference in references.AsEnumerable().Reverse()) reference.Apply(false);
            foreach (var entry in inserted.AsEnumerable().Reverse())
                foreach (var part in entry.Parts.Reverse())
                    if (Asset.Hierarchy.Parts.TryGetValue(part.Design.Entity.Id, out var design)) hierarchyGraph!.RemovePartFromAsset(design);
            RestoreSelection(beforeSelection.Ids, beforeSelection.PrimaryId);
            revision = beforeRevision; StructureVersion++;
        }
        try
        {
            foreach (var parent in parents)
            {
                var result = EntityClipboardService.DeserializeCopiedData(text, Asset,
                    asRoot ? typeof(AssetCompositeHierarchyData<EntityDesign, Entity>) : typeof(Entity));
                if (result.Items.Count == 0 || result.Items.Any(item => item.Processor is not EntityHierarchyPasteProcessor || item.Data is not AssetCompositeHierarchyData<EntityDesign, Entity>))
                    throw new InvalidOperationException("The clipboard does not contain an entity hierarchy.");
                foreach (var item in result.Items)
                {
                    var hierarchy = (AssetCompositeHierarchyData<EntityDesign, Entity>)item.Data;
                    var unresolved = new UnloadableDetector(); unresolved.Visit(hierarchy);
                    if (unresolved.Found) throw new InvalidOperationException("The copied hierarchy contains unresolved types.");
                    if (hierarchy.Parts.Values.Any(part => part.Base != null)) throw new InvalidOperationException("Pasting inherited entities is not supported yet.");
                    foreach (var root in hierarchy.RootParts.ToArray())
                    {
                        if (Subtree(root).Any(entity => Asset.Hierarchy.Parts.ContainsKey(entity.Id))) throw new InvalidOperationException("The pasted hierarchy contains duplicate entity identities.");
                        var index = parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count;
                        var parts = Subtree(root).Select(entity => new DeletedPart(hierarchy.Parts[entity.Id],
                            entity == root ? parent : entity.Transform.Parent?.Entity,
                            entity == root ? index : entity.Transform.Parent!.Children.IndexOf(entity.Transform))).ToArray();
                        inserted.Add((hierarchy, root, parts));
                        hierarchyGraph!.AddPartToAsset(hierarchy.Parts, hierarchy.Parts[root.Id], parent, index);
                    }
                }
            }
            if (inserted.Count == 0) throw new InvalidOperationException("The copied hierarchy is empty.");
            var collector = new HierarchyClipboardReferenceCollector(hierarchyGraph!.Definition,
                inserted.SelectMany(entry => Subtree(entry.Root)).SelectMany(entity => new IIdentifiable[] { entity }.Concat(entity.Components)).ToDictionary(value => value.Id));
            collector.Visit(hierarchyGraph.RootNode); references.AddRange(collector.References);
            foreach (var reference in references) reference.Apply(true);
            RestoreSelection(inserted.Select(entry => entry.Root.Id), inserted[^1].Root.Id);
            revision = afterRevision; StructureVersion++;
            using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(Remove, Insert));
        }
        catch (Exception error)
        {
            try { Remove(); }
            catch (Exception restoreError) { throw new AggregateException("Entity paste and restoration failed.", error, restoreError); }
            throw;
        }
        LastAction = "Paste entities";
    }

    internal void CutSelectedEntities()
    {
        if (!CanCopyEntities || !CanDeleteSelection) throw new InvalidOperationException("The selected entities cannot be cut.");
        if (SessionHistory != null)
        {
            CutHierarchyRootsWithJournal(GetSelectedTransformRoots());
            return;
        }
        EndEdits(); EnsureHierarchyGraph();
        var roots = GetSelectedTransformRoots().OrderBy(entity => entity.Transform.Parent?.Children.IndexOf(entity.Transform)
            ?? Asset.Hierarchy.RootParts.IndexOf(entity)).ToArray();
        var parts = roots.SelectMany(Subtree).Select(entity => new DeletedPart(Asset.Hierarchy.Parts[entity.Id], entity.Transform.Parent?.Entity,
            entity.Transform.Parent?.Children.IndexOf(entity.Transform) ?? Asset.Hierarchy.RootParts.IndexOf(entity))).ToArray();
        var collection = new AssetPartCollection<EntityDesign, Entity>();
        foreach (var part in parts) collection.Add(part.Design);
        var selection = SnapshotSelection(); var beforeRevision = revision; var afterRevision = ++nextRevision;
        void Restore()
        {
            foreach (var part in parts)
                if (!Asset.Hierarchy.Parts.ContainsKey(part.Design.Entity.Id)) hierarchyGraph!.AddPartToAsset(collection, part.Design, part.Parent, part.Index);
            RestoreSelection(selection.Ids, selection.PrimaryId); revision = beforeRevision; StructureVersion++;
        }
        void Remove()
        {
            try
            {
                foreach (var part in parts.Reverse()) hierarchyGraph!.RemovePartFromAsset(part.Design);
                RestoreSelection(selection.Ids, selection.PrimaryId); revision = afterRevision; StructureVersion++;
            }
            catch (Exception error)
            {
                try { Restore(); }
                catch (Exception restoreError) { throw new AggregateException("Entity cut and restoration failed.", error, restoreError); }
                throw;
            }
        }
        Remove();
        try { using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(Restore, Remove)); }
        catch { Restore(); throw; }
        LastAction = "Cut selection";
    }

    private sealed class HierarchyClipboardReferenceCollector(AssetPropertyGraphDefinition definition, Dictionary<Guid, IIdentifiable> targets)
        : IdentifiableObjectVisitorBase(definition)
    {
        internal readonly List<HierarchyClipboardReference> References = [];
        protected override void ProcessIdentifiableMembers(IIdentifiable value, IMemberNode member)
        {
            if (definition.IsMemberTargetObjectReference(member, value) && targets.TryGetValue(value.Id, out var target) && !ReferenceEquals(value, target))
                References.Add(new(member, NodeIndex.Empty, value, target, ((IAssetMemberNode)member).IsContentOverridden()));
        }
        protected override void ProcessIdentifiableItems(IIdentifiable value, IObjectNode collection, NodeIndex index)
        {
            if (definition.IsTargetItemObjectReference(collection, index, value) && targets.TryGetValue(value.Id, out var target) && !ReferenceEquals(value, target))
                References.Add(new(collection, index, value, target, ((IAssetObjectNode)collection).IsItemOverridden(index)));
        }
    }
    private sealed record HierarchyClipboardReference(IGraphNode Node, NodeIndex Index, object Before, object After, bool WasOverridden)
    {
        internal void Apply(bool forward)
        {
            if (Node is IAssetMemberNode member) { member.Update(forward ? After : Before); member.OverrideContent(WasOverridden); }
            else { var collection = (IAssetObjectNode)Node; collection.Update(forward ? After : Before, Index); collection.OverrideItem(WasOverridden, Index); }
        }
    }
}
