using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

// A native module can mutate a graph directly. Guard that path as well as widgets:
// parsed proxy base fields are not the data which ErrorRecoverySerializer writes.
internal sealed class EditorUnknownMutationGuard : IDisposable
{
    private readonly AssetPropertyGraph graph;
    private readonly HashSet<IGraphNode> protectedNodes = [];
    private bool dirty = true;
    internal EditorUnknownMutationGuard(AssetPropertyGraph graph)
    {
        this.graph = graph;
        graph.Changing += MemberChanging; graph.ItemChanging += ItemChanging;
        graph.Changed += MemberChanged; graph.ItemChanged += ItemChanged;
    }
    private void Check(IGraphNode target)
    {
        if (dirty)
        {
            protectedNodes.Clear();
            var visitor = graph.CreateReconcilerVisitor();
            visitor.Visiting += (node, path) =>
            {
                if (UnknownAssetData.IsProxyNode(node)) protectedNodes.Add(node);
                for (var current = path; current != null; current = current.GetParent())
                    if (UnknownAssetData.IsProxyNode(current.GetAccessor().Node)) { protectedNodes.Add(node); break; }
            };
            visitor.Visit(graph.RootNode); dirty = false;
        }
        if (protectedNodes.Contains(target) || UnknownAssetData.IsProxyNode(target))
            throw new InvalidOperationException("This property belongs to an unavailable object type.");
    }
    private void MemberChanging(object? sender, MemberNodeChangeEventArgs args) => Check(args.Member);
    private void ItemChanging(object? sender, ItemChangeEventArgs args) => Check(args.Collection);
    private void MemberChanged(object? sender, AssetMemberNodeChangeEventArgs args)
    { if (args.Member.Target != null || args.NewValue is Stride.Core.Yaml.IUnloadable) dirty = true; }
    private void ItemChanged(object? sender, AssetItemNodeChangeEventArgs args) => dirty = true;
    public void Dispose()
    {
        graph.Changing -= MemberChanging; graph.ItemChanging -= ItemChanging;
        graph.Changed -= MemberChanged; graph.ItemChanged -= ItemChanged;
    }
}
