using Stride.Assets.Entities;
using Stride.Assets.Presentation.ViewModel.CopyPasteProcessors;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private sealed record ClipboardInsertion(AssetCompositeHierarchyData<EntityDesign, Entity> Hierarchy, Entity? Parent);

    private void PrepareClipboardBases(IEnumerable<AssetCompositeHierarchyData<EntityDesign, Entity>> hierarchies)
    {
        var bases = new Dictionary<AssetId, EntityHierarchyDocument>();
        foreach (var hierarchy in hierarchies)
            foreach (var part in hierarchy.Parts.Values)
            {
                if (part.Base is not { } link) continue;
                var source = SessionHistory!.HierarchyGraphs.Resolve?.Invoke(link.BasePartAsset.Id)
                    ?? throw new InvalidOperationException("The copied prefab base cannot be resolved.");
                if (!source.Asset.Hierarchy.Parts.ContainsKey(link.BasePartId))
                    throw new InvalidOperationException("The copied prefab part no longer exists.");
                var pending = new Stack<EntityHierarchyDocument>(); pending.Push(source);
                var visited = new HashSet<AssetId>();
                while (pending.TryPop(out var current))
                {
                    if (current.Asset.Id == Asset.Id) throw new InvalidOperationException("The copied hierarchy depends on its destination asset.");
                    if (!visited.Add(current.Asset.Id)) continue;
                    foreach (var inherited in current.Asset.Hierarchy.Parts.Values.Select(design => design.Base).OfType<BasePart>())
                        pending.Push(SessionHistory.HierarchyGraphs.Resolve?.Invoke(inherited.BasePartAsset.Id)
                            ?? throw new InvalidOperationException("A copied prefab base cannot be resolved."));
                }
                bases.TryAdd(source.Asset.Id, source);
            }
        try
        {
            foreach (var source in bases.Values) SessionHistory!.HierarchyGraphs.RegisterPreparedBase(this, source);
        }
        catch (Exception failure) { RefreshClipboardBasesAfterFailure(failure); throw; }
    }

    private void PasteEntitiesWithJournal(string text, Entity?[] parents, string targetFolder = "", bool allowEmptyFolderPayload = false)
    {
        EndEdits(); EnsureHierarchyGraph();
        if (parents.Length == 0 || parents.Any(parent => !CanCreateEntity(parent))) throw new InvalidOperationException("The paste destination is not editable.");
        var stagedTarget = (EntityHierarchyAssetBase)Activator.CreateInstance(Asset.GetType())!;
        stagedTarget.Id = Asset.Id;
        foreach (var part in Asset.Hierarchy.Parts.Values) stagedTarget.Hierarchy.Parts.Add(part);
        var insertions = new List<ClipboardInsertion>();
        foreach (var parent in parents)
        {
            // The original processor sees already staged identities, just as the WPF
            // sequential paste sees parts inserted for earlier selected parents.
            var result = EntityClipboardService.DeserializeCopiedData(text, stagedTarget,
                parent == null ? typeof(AssetCompositeHierarchyData<EntityDesign, Entity>) : typeof(Entity));
            if (result.Items.Count == 0 || result.Items.Any(item => item.Processor is not EntityHierarchyPasteProcessor
                || item.Data is not AssetCompositeHierarchyData<EntityDesign, Entity>))
                throw new InvalidOperationException("The clipboard does not contain an entity hierarchy.");
            foreach (var item in result.Items)
            {
                var hierarchy = (AssetCompositeHierarchyData<EntityDesign, Entity>)item.Data;
                var unresolved = new UnloadableDetector(); unresolved.Visit(hierarchy);
                if (unresolved.Found) throw new InvalidOperationException("The copied hierarchy contains unresolved types.");
                foreach (var part in hierarchy.Parts.Values)
                {
                    if (stagedTarget.Hierarchy.Parts.ContainsKey(part.Entity.Id)) throw new InvalidOperationException("The copied hierarchy contains conflicting identities.");
                    stagedTarget.Hierarchy.Parts.Add(part);
                }
                foreach (var root in hierarchy.RootParts)
                {
                    var design = hierarchy.Parts[root.Id];
                    design.Folder = string.IsNullOrEmpty(design.Folder) ? targetFolder
                        : targetFolder.Length == 0 ? design.Folder : targetFolder + "/" + design.Folder;
                }
                insertions.Add(new(hierarchy, parent));
            }
        }
        if (insertions.Count == 0 || insertions.All(entry => entry.Hierarchy.RootParts.Count == 0))
        {
            if (allowEmptyFolderPayload && insertions.Count > 0 && insertions.All(entry => entry.Hierarchy.Parts.Count == 0)) return;
            throw new InvalidOperationException("The copied hierarchy is empty.");
        }
        PrepareClipboardBases(insertions.Select(entry => entry.Hierarchy));
        try
        {
            RecordClipboardGroup(() =>
            {
                ApplyInheritedGraphMutation(() =>
                {
                    foreach (var entry in insertions)
                        foreach (var root in entry.Hierarchy.RootParts)
                            HierarchyGraph.AddPartToAsset(entry.Hierarchy.Parts, entry.Hierarchy.Parts[root.Id], entry.Parent,
                                entry.Parent?.Transform.Children.Count ?? Asset.Hierarchy.RootParts.Count);
                    RepairClipboardIncomingReferences();
                }, "Paste entities");
            }, "Paste entities");
        }
        catch (Exception failure) { RefreshClipboardBasesAfterFailure(failure); throw; }
    }

    private void DuplicateEntitiesWithJournal(Entity[]? requestedRoots = null, Guid[]? preservedSelection = null)
    {
        EndEdits(); EnsureHierarchyGraph();
        var roots = requestedRoots ?? GetSelectedTransformRoots();
        if (!CanCopyHierarchyRoots(roots)) throw new InvalidOperationException("The selected clipboard closure is unavailable.");
        if (roots.Any(root => !CanCreateEntity(root.Transform.Parent?.Entity))) throw new InvalidOperationException("The duplication destination is not editable.");
        var copies = roots.Select(root =>
        {
            var hierarchy = AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>.CloneSubHierarchies(
                HierarchyGraph.Container.NodeContainer, Asset, [root.Id], SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects, out _);
            AssetPartsAnalysis.GenerateNewBaseInstanceIds(hierarchy);
            return (Source: root, Hierarchy: hierarchy);
        }).ToArray();
        PrepareClipboardBases(copies.Select(copy => copy.Hierarchy));
        try
        {
            ApplyInheritedGraphMutation(() =>
            {
                foreach (var copy in copies)
                {
                    var parent = copy.Source.Transform.Parent?.Entity;
                    var index = (parent?.Transform.Children.IndexOf(copy.Source.Transform) ?? Asset.Hierarchy.RootParts.IndexOf(copy.Source)) + 1;
                    var root = copy.Hierarchy.RootParts.Single();
                    HierarchyGraph.AddPartToAsset(copy.Hierarchy.Parts, copy.Hierarchy.Parts[root.Id], parent, index);
                }
                var inserted = copies.Select(copy => copy.Hierarchy.RootParts.Single()).ToArray();
                RestoreSelection((preservedSelection ?? []).Concat(inserted.Select(root => root.Id)), inserted[^1].Id);
            }, "Duplicate entities");
        }
        catch (Exception failure) { RefreshClipboardBasesAfterFailure(failure); throw; }
    }

    private void CutHierarchyRootsWithJournal(IReadOnlyCollection<Entity> roots)
    {
        EndEdits(); EnsureHierarchyGraph();
        if (roots.Count == 0 || roots.Any(root => !CanDeleteEntity(root))) throw new InvalidOperationException("The selected entities cannot be cut.");
        var parts = roots.SelectMany(Subtree).DistinctBy(entity => entity.Id).Reverse().Select(entity => Asset.Hierarchy.Parts[entity.Id]).ToArray();
        var folders = CapturePartRemovalFolders(roots);
        RecordHierarchyFolderGroup(() =>
        {
            ApplyInheritedGraphMutation(() =>
            {
                // WPF cut keeps incoming references and does not mark instance parts deleted.
                foreach (var part in parts) HierarchyGraph.RemovePartFromAsset(part);
            }, "Cut selection");
            RetainPartRemovalFolders(folders, "Cut selection");
        }, "Cut selection");
    }

    private void RecordClipboardGroup(Action mutate, string title)
    {
        if (SessionHistory!.IsCapturingOperations) { mutate(); return; }
        using var transaction = CreateDocumentTransaction(title);
        using var capture = SessionHistory.CaptureOperationsAtomic(mutate);
        capture.Publish(SessionHistory, title);
    }

    private void RefreshClipboardBasesAfterFailure(Exception failure)
    {
        try { SessionHistory!.HierarchyGraphs.Refresh(this); }
        catch (Exception recovery) { throw new AggregateException("Clipboard mutation and base-graph restoration failed.", failure, recovery); }
    }

    private void RepairClipboardIncomingReferences()
    {
        var targets = IdentifiableObjectCollector.Collect(HierarchyGraph.Definition, HierarchyGraph.RootNode);
        var own = new ClipboardReferenceRepair(HierarchyGraph.Definition, targets, _ => true);
        own.Visit(HierarchyGraph.RootNode);
        if (own.Nodes.Any(node => !CanEditHierarchyNode(node))) throw new InvalidOperationException("An incoming reference is sealed.");
        foreach (var change in own.Changes) change();
        foreach (var state in SessionHistory!.Registry.PublishedStates.ToArray())
        {
            if (state.Document is not EntityHierarchyDocument other || ReferenceEquals(other, this) || !other.CanSave) continue;
            var repair = new ClipboardReferenceRepair(other.HierarchyGraph.Definition, targets, value =>
                HierarchyGraph.Container.NodeContainer.GetOrCreateNode(value) is IAssetNode node && ReferenceEquals(node.PropertyGraph, HierarchyGraph));
            repair.Visit(other.HierarchyGraph.RootNode);
            if (repair.Changes.Count == 0) continue;
            if (repair.Nodes.Any(node => !other.CanEditHierarchyNode(node))) throw new InvalidOperationException("An incoming reference is sealed.");
            other.ApplyInheritedGraphMutation(() => { foreach (var change in repair.Changes) change(); }, "Restore pasted entity references");
        }
    }

    private sealed class ClipboardReferenceRepair(AssetPropertyGraphDefinition definition, IReadOnlyDictionary<Guid, IIdentifiable> targets, Func<IIdentifiable, bool> belongsToTarget)
        : IdentifiableObjectVisitorBase(definition)
    {
        internal List<Action> Changes { get; } = [];
        internal List<IGraphNode> Nodes { get; } = [];
        protected override void ProcessIdentifiableMembers(IIdentifiable value, IMemberNode member)
        {
            if (!definition.IsMemberTargetObjectReference(member, value) || !targets.TryGetValue(value.Id, out var target)
                || ReferenceEquals(value, target) || !belongsToTarget(value)) return;
            var node = (IAssetMemberNode)member; var before = node.GetContentOverride();
            Nodes.Add(node); Changes.Add(() => { node.Update(target); node.RestoreContentOverride(before); });
        }
        protected override void ProcessIdentifiableItems(IIdentifiable value, IObjectNode collection, NodeIndex index)
        {
            if (!definition.IsTargetItemObjectReference(collection, index, value) || !targets.TryGetValue(value.Id, out var target)
                || ReferenceEquals(value, target) || !belongsToTarget(value)) return;
            var node = (IAssetObjectNode)collection; var before = node.GetItemOverride(index); var key = node.GetKeyOverride(index);
            Nodes.Add(node); Changes.Add(() => { node.Update(target, index); node.RestoreItemOverrides(index, before, key); });
        }
    }
}
