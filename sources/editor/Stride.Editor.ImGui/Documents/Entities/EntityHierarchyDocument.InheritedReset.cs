using Stride.Assets.Quantum;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanResetInheritedComponent(EntityComponent component) => TryGetInheritedResetRoot(component.Entity, component, out _);
    internal bool CanResetInheritedEntity(Entity entity) => TryGetInheritedResetRoot(entity, entity, out _);
    internal void ResetInheritedComponent(EntityComponent component) => ResetInheritedRoot(component.Entity, component);
    internal void ResetInheritedEntity(Entity entity) => ResetInheritedRoot(entity, entity);
    private bool TryGetInheritedResetRoot(Entity? entity, object root, out IAssetNode? node)
    {
        node = null;
        if (!CanSave || SessionHistory == null || entity == null
            || historyScope.AuthoringState is not { IsPublished: true } state
            || !ReferenceEquals(SessionHistory.Registry.Find(Asset.Id), state)
            || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) || !ReferenceEquals(design.Entity, entity) || design.Base == null
            || root is EntityComponent component && !entity.Components.Contains(component)
            || HierarchyGraph.Container.TryGetGraph(design.Base.BasePartAsset.Id) is not EntityHierarchyPropertyGraph baseGraph
            || !baseGraph.Asset.Hierarchy.Parts.ContainsKey(design.Base.BasePartId)) return false;
        node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(root) as IAssetNode;
        return node != null && CanResetHierarchyNode(node);
    }
    private void ResetInheritedRoot(Entity? entity, object root)
    {
        EndEdits();
        if (!TryGetInheritedResetRoot(entity, root, out var node)) throw new InvalidOperationException("This object has no editable prefab base.");
        ApplyInheritedGraphMutation(() => node!.ResetOverrideRecursively(), "Reset to base value");
    }
    internal void ApplyInheritedGraphMutation(Action mutate, string title)
        => ApplyInheritedGraphMutation(mutate, title, out _);
    internal void ApplyInheritedGraphMutation(Action mutate, string title, IDisposable[] resources)
    {
        var transferred = false;
        try { ApplyInheritedGraphMutation(mutate, title, out _, resources, () => transferred = true); }
        finally { if (!transferred) foreach (var resource in resources) resource.Dispose(); }
    }
    private Guid ApplyPreparedReferenceMutation(Action mutate, string title)
    {
        ApplyInheritedGraphMutation(mutate, title, out var id);
        return id;
    }
    private void ApplyInheritedGraphMutation(Action mutate, string title, out Guid operationId,
        IDisposable[]? resources = null, Action? resourcesTransferred = null)
    {
        EndEdits();
        var graph = HierarchyGraph;
        using var unknownGuard = new EditorUnknownMutationGuard(graph);
        var before = new AssetGraphOverrideSnapshot(graph, graph.RootNode);
        var beforeRevision = revision;
        var changes = new List<HierarchyNodeChange>();
        var unfinished = new EditorQuantumPendingChanges(graph);
        var operation = new HierarchyResetOperation(this, graph, graph.RootNode, before, changes, unfinished, beforeRevision, ++nextRevision);
        Operation recordedOperation = resources == null ? operation : new RetainedOperation(operation, resources);
        resourcesTransferred?.Invoke();
        operationId = operation.Id;
        var published = false;
        EditorOperationCapture? propagated = null;
        void MemberChanged(object? _, AssetMemberNodeChangeEventArgs change)
        {
            changes.Add(new((IAssetNode)change.Member, NodeIndex.Empty, ContentChangeType.ValueChange, change.OldValue, change.NewValue, change.ItemId));
            if (!EditorHierarchyGraphs.IsScalar(change.Member.Type)) InvalidateHierarchyAuthoringPaths();
        }
        void ItemChanged(object? _, AssetItemNodeChangeEventArgs change)
        {
            changes.Add(new((IAssetNode)change.Collection, change.Index, change.ChangeType, change.OldValue, change.NewValue, change.ItemId));
            InvalidateHierarchyAuthoringPaths();
        }
        suppressOwnScalarRecording++;
        graph.Changed += MemberChanged; graph.ItemChanged += ItemChanged;
        try
        {
            using var transaction = CreateDocumentTransaction(title);
            if (SessionHistory == null || SessionHistory.IsCapturingOperations) mutate();
            else propagated = SessionHistory.CaptureOperationsAtomic(mutate);
            graph.Changed -= MemberChanged; graph.ItemChanged -= ItemChanged;
            unfinished.Dispose();
            operation.CompleteInitial();
            try
            {
                if (SessionHistory == null) RecordOperation(recordedOperation);
                else if (propagated == null) SessionHistory.PushOperation(recordedOperation, historyScope.AuthoringState!);
                else SessionHistory.PushOperation(new CompositeOperation(propagated.Entries.Select(entry => entry.Operation).Append(recordedOperation)),
                    propagated.Participants.Append(historyScope.AuthoringState!).Distinct().ToArray());
                published = true;
            }
            catch { if (propagated != null) SessionHistory.RestoreFailedCapture(propagated); throw; }
        }
        catch (Exception failure)
        {
            graph.Changed -= MemberChanged; graph.ItemChanged -= ItemChanged;
            if (!published)
            {
                try
                {
                    if (SessionHistory == null) operation.RestoreInitial();
                    else SessionHistory.CompensateInitialMutation(operation.RestoreInitial);
                    if (resources != null) ((IOperation)recordedOperation).Freeze();
                }
                catch (Exception recovery)
                {
                    SessionHistory?.RetainFailedInitialOperation(recordedOperation, historyScope.AuthoringState!);
                    throw new AggregateException("Prefab reset and restoration failed.", failure, recovery);
                }
            }
            throw;
        }
        finally
        {
            graph.Changed -= MemberChanged; graph.ItemChanged -= ItemChanged;
            unfinished.Dispose();
            suppressOwnScalarRecording--;
            propagated?.Dispose();
        }
        LastAction = title;
    }
    private sealed record HierarchyNodeChange(IAssetNode Node, NodeIndex Index, ContentChangeType Type, object? Before, object? After, ItemId Item)
    {
        internal void Apply(bool forward)
        {
            if (Type == ContentChangeType.ValueChange) { ((IAssetMemberNode)Node).Update(forward ? After : Before); return; }
            var collection = (IAssetObjectNode)Node;
            var adding = Type == ContentChangeType.CollectionAdd;
            if (Type == ContentChangeType.CollectionUpdate)
            {
                var index = Item == ItemId.Empty ? Index : collection.IdToIndex(Item);
                collection.Update(forward ? After : Before, index); return;
            }
            var value = adding ? After : Before;
            if (forward == adding)
            {
                if (Item != ItemId.Empty && collection.HasId(Item)) return;
                if (Item == ItemId.Empty) collection.Add(value!, Index);
                else if (Index.IsEmpty) collection.Restore(value!, Item);
                else collection.Restore(value!, Index, Item);
            }
            else
            {
                if (Item != ItemId.Empty && !collection.HasId(Item)) return;
                var index = Item == ItemId.Empty ? Index : collection.IdToIndex(Item);
                collection.Remove(value!, index);
            }
        }
    }
    private sealed class HierarchyResetOperation(EntityHierarchyDocument document, EntityHierarchyPropertyGraph graph, IAssetNode root,
        AssetGraphOverrideSnapshot before, List<HierarchyNodeChange> changes, EditorQuantumPendingChanges unfinished, long beforeRevision, long afterRevision) : Operation
    {
        private AssetGraphOverrideSnapshot? after;
        private bool? hasEffect;
        private int initialRecoveryIndex = -1;
        private readonly EntitySelectionSnapshot beforeSelection = document.SnapshotSelection();
        private readonly Dictionary<IAssetNode, Guid> componentOwners = CaptureComponentNodes(document, graph);
        private EntitySelectionSnapshot afterSelection;
        private static Dictionary<IAssetNode, Guid> CaptureComponentNodes(EntityHierarchyDocument owner, EntityHierarchyPropertyGraph actualGraph)
        {
            var result = new Dictionary<IAssetNode, Guid>();
            foreach (var component in owner.Asset.Hierarchy.Parts.Values.SelectMany(part => part.Entity.Components))
            {
                var visitor = actualGraph.CreateReconcilerVisitor();
                visitor.Visiting += (node, _) => { if (node is IAssetNode asset) result.TryAdd(asset, component.Id); };
                visitor.Visit(actualGraph.Container.NodeContainer.GetOrCreateNode(component));
            }
            return result;
        }
        public override bool HasEffect => hasEffect ?? (changes.Count != 0 || !before.IsCurrent);
        internal void CompleteInitial()
        {
            hasEffect = changes.Count != 0 || !before.IsCurrent;
            after = new(graph, root);
            if (HasEffect) Complete(afterRevision);
            afterSelection = document.SnapshotSelection();
        }
        private void Complete(long value)
        {
            document.lastHierarchyCapturedChanges = changes.Count;
            document.lastHierarchyStructuralChanges = changes.Count(change => change.Type != ContentChangeType.ValueChange || !EditorHierarchyGraphs.IsScalar(change.Node.Type));
            document.lastHierarchyAffectedComponents = changes.Where(change => change.Type != ContentChangeType.ValueChange || !EditorHierarchyGraphs.IsScalar(change.Node.Type))
                .Where(change => !graph.IsChildPartReference(change.Node, change.Index))
                .Where(change => componentOwners.ContainsKey(change.Node)).Select(change => componentOwners[change.Node]).Distinct().Count();
            document.revision = value;
            document.HasInheritedHierarchy = document.Asset.Hierarchy.Parts.Values.Any(part => part.Base != null);
            document.PruneSelection();
            if (changes.Any(change => change.Type != ContentChangeType.ValueChange || !EditorHierarchyGraphs.IsScalar(change.Node.Type)))
            {
                foreach (var id in changes.Where(change => change.Type != ContentChangeType.ValueChange || !EditorHierarchyGraphs.IsScalar(change.Node.Type))
                    .Where(change => !graph.IsChildPartReference(change.Node, change.Index))
                    .Where(change => componentOwners.ContainsKey(change.Node)).Select(change => componentOwners[change.Node]).Distinct())
                    document.componentReplacementVersions[id] = document.ComponentReplacementVersion(id) + 1;
                document.InvalidateComponentStructure();
            }
        }
        internal void RestoreInitial()
        {
            unfinished.Restore();
            if (initialRecoveryIndex < 0) initialRecoveryIndex = changes.Count;
            while (initialRecoveryIndex > 0) { changes[initialRecoveryIndex - 1].Apply(false); initialRecoveryIndex--; }
            before.Restore(); graph.RefreshBase(); Complete(beforeRevision);
            document.RestoreSelection(beforeSelection.Ids, beforeSelection.PrimaryId);
        }
        protected override void Rollback()
        {
            if (after == null || initialRecoveryIndex >= 0) { RestoreInitial(); return; }
            Apply(false);
        }
        protected override void Rollforward() => Apply(true);
        private void Apply(bool forward)
        {
            var current = new AssetGraphOverrideSnapshot(graph, root);
            var currentRevision = document.revision;
            var currentSelection = document.SnapshotSelection();
            var completed = new List<HierarchyNodeChange>();
            using var incomplete = new EditorQuantumPendingChanges(graph);
            var propagation = graph.Container.PropagateChangesFromBase;
            graph.Container.PropagateChangesFromBase = false;
            try
            {
                foreach (var change in forward ? changes : changes.AsEnumerable().Reverse())
                { completed.Add(change); change.Apply(forward); }
                (forward ? after! : before).Restore(); graph.RefreshBase(); Complete(forward ? afterRevision : beforeRevision);
                var selection = forward ? afterSelection : beforeSelection;
                document.RestoreSelection(selection.Ids, selection.PrimaryId);
            }
            catch (Exception failure)
            {
                try
                {
                    incomplete.Restore();
                    foreach (var change in completed.AsEnumerable().Reverse()) change.Apply(!forward);
                    current.Restore(); graph.RefreshBase(); Complete(currentRevision);
                    document.RestoreSelection(currentSelection.Ids, currentSelection.PrimaryId);
                }
                catch (Exception recovery) { throw new AggregateException("Prefab reset replay and compensation failed.", failure, recovery); }
                throw;
            }
            finally { graph.Container.PropagateChangesFromBase = propagation; }
        }
    }
}
