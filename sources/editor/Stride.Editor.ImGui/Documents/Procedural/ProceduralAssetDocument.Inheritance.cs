using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    private readonly HashSet<IAssetNode> authoringNodes = [];
    private readonly HashSet<IAssetNode> inheritedSealedNodes = [];
    private readonly HashSet<IAssetNode> unknownAuthoringNodes = [];
    internal AssetPropertyGraph AuthoringGraph => graph ?? throw new InvalidOperationException("The procedural graph is unresolved.");
    AssetPropertyGraph IEditorAssetGraphDocument.AuthoringGraph => AuthoringGraph;
    void IEditorAssetGraphDocument.ApplyInheritedAssetMutation(Action mutate, string title) => ApplyInheritedAssetMutation(mutate, title);
    void IEditorAssetGraphDocument.UpdateAuthoringAssetIdentity(AssetItem canonical) => AuthoringGraph.UpdateAssetItemIdentity(canonical);

    internal bool CanEditAssetSettingNode(IGraphNode node)
        => CanEdit && node is IAssetNode asset && authoringNodes.Contains(asset) && !inheritedSealedNodes.Contains(asset) && !unknownAuthoringNodes.Contains(asset)
        && (node is not IMemberNode member || AssetMetadataDocument.CanAssignAuthoringNode(member))
        && (SessionHistory == null || historyScope.AuthoringState is { } state
            && (ReferenceEquals(SessionHistory.Registry.Find(Asset.Id), state) || SessionHistory.IsCapturingOperations && !state.IsPublished));
    private void RefreshAuthoringNodeAccess()
    {
        authoringNodes.Clear(); inheritedSealedNodes.Clear(); unknownAuthoringNodes.Clear();
        if (graph == null) return;
        var visitor = graph.CreateReconcilerVisitor();
        visitor.Visiting += (node, path) =>
        {
            if (node is not IAssetNode asset) return;
            authoringNodes.Add(asset);
            if (UnknownAssetData.IsProxyNode(node)) unknownAuthoringNodes.Add(asset);
            for (var current = path; current != null; current = current.GetParent())
            {
                var accessor = current.GetAccessor();
                if (UnknownAssetData.IsProxyNode(accessor.Node)) unknownAuthoringNodes.Add(asset);
                if (accessor.Node is IAssetNode parent && graph.IsInheritedNodeSealed(parent, accessor.Index))
                { inheritedSealedNodes.Add(asset); break; }
            }
        };
        visitor.Visit(graph.RootNode);
    }
    internal bool CanResetAssetSettingNode(IGraphNode node)
        => CanEditAssetSettingNode(node) && node is IAssetNode { BaseNode: not null };
    internal void ResetAssetSettingNode(IGraphNode node)
    {
        EndEdit();
        if (!CanResetAssetSettingNode(node)) throw new InvalidOperationException("This property has no editable base value.");
        ApplyInheritedAssetMutation(((IAssetNode)node).ResetOverrideRecursively, "Reset to base value");
    }
    private EditorQuantumMutation CreateMutationJournal()
    {
        var before = revision; var after = ++nextRevision;
        return new(AuthoringGraph, () => { revision = before; RefreshFields(); }, () => { revision = after; RefreshFields(); });
    }
    internal void ApplyInheritedAssetMutation(Action mutate, string title)
    {
        EndEdit();
        PublishMutation(CreateMutationJournal(), title, mutate);
    }
    internal Guid ApplyPreparedAssetMutation(Action mutate, string title)
    {
        EndEdit();
        var journal = CreateMutationJournal();
        PublishMutation(journal, title, mutate);
        return journal.Id;
    }
    private void PublishMutation(EditorQuantumMutation journal, string title, Action mutate)
    {
        using var unknownGuard = new EditorUnknownMutationGuard(AuthoringGraph);
        EditorOperationCapture? propagated = null; var published = false;
        try
        {
            using var transaction = CreateDocumentTransaction(title);
            if (SessionHistory == null || SessionHistory.IsCapturingOperations) mutate();
            else propagated = SessionHistory.CaptureOperationsAtomic(() => { mutate(); SessionHistory.AssetGraphs.Flush(this); });
            journal.Complete();
            try
            {
                if (SessionHistory == null) RecordOperation(journal);
                else
                {
                    var owner = historyScope.AuthoringState ?? throw new InvalidOperationException("The asset has no authored owner.");
                    if (propagated == null) SessionHistory.PushOperation(journal, owner);
                    else SessionHistory.PushOperation(new CompositeOperation(propagated.Entries.Select(entry => entry.Operation).Append(journal)),
                        propagated.Participants.Append(owner).Distinct().ToArray());
                }
                published = true;
            }
            catch { if (propagated != null) SessionHistory!.RestoreFailedCapture(propagated); throw; }
        }
        catch (Exception failure)
        {
            if (!published)
                try
                {
                    if (SessionHistory != null) SessionHistory.CompensateInitialMutation(journal.RestoreInitial); else journal.RestoreInitial();
                }
                catch (Exception recovery)
                {
                    if (SessionHistory != null) SessionHistory.RetainFailedInitialOperation(journal, historyScope.AuthoringState!);
                    throw new AggregateException("Procedural changes and restoration failed.", failure, recovery);
                }
            throw;
        }
        finally { journal.Dispose(); propagated?.Dispose(); }
    }
}
