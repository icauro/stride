using Stride.Core.Assets.Quantum.Internal;
using Stride.Core.Assets.Quantum.Visitors;

namespace Stride.Core.Assets.Quantum;

/// <summary>Exact override and collection identity state of actual retained Quantum nodes.</summary>
public sealed class AssetGraphOverrideSnapshot
{
    private readonly List<(Action Restore, Func<bool> IsCurrent)> entries = [];
    public AssetGraphOverrideSnapshot(AssetPropertyGraph graph, IAssetNode root)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(root);
        if (!ReferenceEquals(root.PropertyGraph, graph)) throw new ArgumentException("The root must belong to this graph.", nameof(root));
        if (ReferenceEquals(root, graph.RootNode)) entries.Add(graph.CaptureReconciliationState());
        var visitor = new AssetGraphVisitorBase(graph.Definition);
        visitor.Visiting += (node, _) =>
        {
            if (node is IAssetMemberNode member)
            {
                var flags = member.GetContentOverride();
                entries.Add((() => member.RestoreContentOverride(flags), () => member.GetContentOverride() == flags));
            }
            if (node is IAssetObjectNodeInternal collection) entries.Add(collection.CaptureOverrideState());
        };
        visitor.Visit(root);
    }
    public bool IsCurrent => entries.All(entry => entry.IsCurrent());
    public void Restore()
    {
        List<Exception>? failures = null;
        foreach (var entry in entries)
            try { entry.Restore(); } catch (Exception error) { (failures ??= []).Add(error); }
        if (failures != null) throw new AggregateException("Quantum override state restoration is incomplete.", failures);
    }
}
