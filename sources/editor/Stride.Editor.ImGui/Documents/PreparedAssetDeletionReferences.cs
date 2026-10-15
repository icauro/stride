using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Reflection;
using Stride.Core.Serialization.Contents;
using Stride.Core.Transactions;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed class PreparedAssetDeletionReferences
{
    private readonly ClearOperation? operation;
    private readonly Action? nativeApply;
    private readonly Func<long>? nativeRevision;
    private readonly AssetDeletionReferenceChoice[] nativeChoices = [];
    private readonly Action<Operation> record;
    private readonly Action undoRevision;
    private bool applied;
    public bool HasChanges => operation?.HasEffect ?? nativeChoices.Length > 0;
    public Guid OperationId => operation?.Id ?? Guid.Empty;
    public long BeforeRevision { get; }
    public long AfterRevision { get; private set; }
    public IReadOnlyList<AssetDeletionReferenceChoice> Choices => operation?.Choices ?? nativeChoices;
    public PreparedAssetDeletionReferences(Asset asset, IReadOnlySet<AssetId> deleted, long beforeRevision, long afterRevision,
        Action<Operation> record, Action undoRevision, Action redoRevision)
    {
        BeforeRevision = beforeRevision; AfterRevision = afterRevision; this.record = record; this.undoRevision = undoRevision;
        operation = new ClearOperation(asset, deleted, undoRevision, redoRevision);
    }
    internal PreparedAssetDeletionReferences(AssetPropertyGraph graph, IReadOnlySet<AssetId> deleted, long beforeRevision,
        Func<long> currentRevision, Func<IGraphNode, NodeIndex, bool> canEdit, Action<Action> mutate)
    {
        BeforeRevision = AfterRevision = beforeRevision; nativeRevision = currentRevision;
        record = _ => { }; undoRevision = () => { };
        nativeChoices = AssetReferenceAnalysis.Visit(graph.AuthoredItem.Asset).Where(link => link.Reference is IReference reference && deleted.Contains(reference.Id))
            .Select(link => new AssetDeletionReferenceChoice(link.Path.Clone(), ((IReference)link.Reference!).Id, link.Path.GetValue(graph.AuthoredItem.Asset)!)).ToArray();
        var itemIds = new Dictionary<Guid, ItemId>();
        (IGraphNode Node, NodeIndex Index) Resolve(AssetDeletionReferenceChoice choice)
        {
            var path = GraphNodePath.From(graph.RootNode, choice.Path, out var index);
            var node = path.GetNode();
            if (node is IAssetObjectNode collection && !index.IsEmpty)
            {
                if (itemIds.TryGetValue(choice.Id, out var itemId))
                {
                    if (!collection.HasId(itemId)) throw new InvalidOperationException("The referenced collection item no longer exists.");
                    index = collection.IdToIndex(itemId);
                }
                else if (collection.TryIndexToId(index, out var captured) && captured != ItemId.Empty) itemIds.Add(choice.Id, captured);
            }
            if (node == null || !canEdit(node, index)) throw new InvalidOperationException("These asset references can't be edited.");
            return (node, index);
        }
        foreach (var choice in nativeChoices) Resolve(choice);
        nativeApply = () =>
        {
            if (nativeChoices.Any(choice => !choice.IsResolved)) throw new InvalidOperationException("Select a replacement or clear this reference.");
            mutate(() =>
            {
                foreach (var choice in nativeChoices)
                {
                    var target = Resolve(choice);
                    if (target.Node is IMemberNode member) member.Update(choice.After);
                    else if (target.Node is IObjectNode collection) collection.Update(choice.After, target.Index);
                    else throw new InvalidOperationException("This reference has no native authored node.");
                }
            });
        };
    }
    public void Apply()
    {
        if (nativeApply != null) { nativeApply(); AfterRevision = nativeRevision!(); }
        else operation!.Apply(true);
        applied = true;
    }
    public void Commit() { if (operation != null) record(operation); }
    public void Restore()
    {
        // Native journals are restored by the enclosing atomic session capture.
        if (operation == null) return;
        if (applied) operation.Apply(false); undoRevision(); operation.Cancelled = true;
    }
    private sealed class ClearOperation : Operation
    {
        private readonly Asset asset;
        public AssetDeletionReferenceChoice[] Choices { get; }
        private object?[]? resolvedValues;
        private readonly Action undoRevision, redoRevision;
        public bool Cancelled { get; set; }
        public override bool HasEffect => !Cancelled && Choices.Length > 0;
        public ClearOperation(Asset asset, IReadOnlySet<AssetId> deleted, Action undoRevision, Action redoRevision)
        {
            this.asset = asset; this.undoRevision = undoRevision; this.redoRevision = redoRevision;
            if (AssetReferenceAnalysis.Visit(asset).Any(link => link.Reference is IReference reference && deleted.Contains(reference.Id)
                && link.Path.Decompose().Last() is MemberPath.SetPathItem)) throw new InvalidOperationException("Set references require their native authored graph.");
            Choices = AssetReferenceAnalysis.Visit(asset).Where(link => link.Reference is IReference reference && deleted.Contains(reference.Id))
                .Select(link => new AssetDeletionReferenceChoice(link.Path.Clone(), ((IReference)link.Reference!).Id, link.Path.GetValue(asset)!)).ToArray();
        }
        public void Apply(bool clear)
        {
            if (clear && resolvedValues == null)
            {
                if (Choices.Any(choice => !choice.IsResolved)) throw new InvalidOperationException("Select a replacement or clear this reference.");
                resolvedValues = Choices.Select(choice => choice.After).ToArray();
            }
            var completed = new List<int>();
            try
            {
                for (var index = 0; index < Choices.Length; index++)
                {
                    var choice = Choices[index];
                    if (!choice.Path.Apply(asset, MemberPathAction.ValueSet, clear ? resolvedValues![index] : choice.Before))
                        throw new InvalidOperationException("This reference can't be edited.");
                    completed.Add(index);
                }
            }
            catch
            {
                foreach (var index in completed.AsEnumerable().Reverse())
                    if (!Choices[index].Path.Apply(asset, MemberPathAction.ValueSet, clear ? Choices[index].Before : resolvedValues![index]))
                        throw new InvalidOperationException("The reference edit could not be restored.");
                throw;
            }
        }
        protected override void Rollback() { if (!Cancelled) { Apply(false); undoRevision(); } }
        protected override void Rollforward() { if (!Cancelled) { Apply(true); redoRevision(); } }
    }
}
