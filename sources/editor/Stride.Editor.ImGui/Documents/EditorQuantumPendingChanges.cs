using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Only unfinished real Quantum mutations; successful changes belong to the operation journal.</summary>
internal sealed class EditorQuantumPendingChanges : IDisposable
{
    private sealed record Pending(IGraphNode Node, GraphNodeContentSnapshot Contents, AssetGraphOverrideSnapshot Overrides);
    private readonly AssetPropertyGraph graph;
    private readonly List<Pending> pending = [];
    private bool subscribed = true;
    internal EditorQuantumPendingChanges(AssetPropertyGraph graph)
    {
        this.graph = graph;
        graph.Changing += MemberChanging; graph.ItemChanging += ItemChanging;
        graph.Changed += MemberCompleted; graph.ItemChanged += ItemCompleted;
    }
    private void Capture(IGraphNode node) => pending.Add(new(node, new(node), new(graph, (IAssetNode)node)));
    private void MemberChanging(object? _, MemberNodeChangeEventArgs e) => Capture(e.Member);
    private void ItemChanging(object? _, ItemChangeEventArgs e) => Capture(e.Collection);
    private void MemberCompleted(object? _, AssetMemberNodeChangeEventArgs e) => Complete(e.Member);
    private void ItemCompleted(object? _, AssetItemNodeChangeEventArgs e) => Complete(e.Collection);
    internal void Complete(IGraphNode node)
    {
        var index = pending.FindLastIndex(entry => ReferenceEquals(entry.Node, node));
        if (index >= 0) pending.RemoveAt(index);
    }
    internal void Restore()
    {
        Dispose();
        while (pending.Count != 0)
        {
            var entry = pending[^1];
            entry.Contents.Restore(); entry.Overrides.Restore(); graph.RefreshNodeListener();
            pending.RemoveAt(pending.Count - 1);
        }
    }
    public void Dispose()
    {
        if (!subscribed) return;
        graph.Changing -= MemberChanging; graph.ItemChanging -= ItemChanging; subscribed = false;
        graph.Changed -= MemberCompleted; graph.ItemChanged -= ItemCompleted;
    }
}
