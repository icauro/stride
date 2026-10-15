using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed class CurveAuthoringContext(EditorAuthoringState owner, Asset asset, AssetPropertyGraph graph,
    Func<IGraphNode, bool> canEdit, Func<IAssetObjectNode, NodeIndex, bool> canEditItem,
    Action<Action, string> mutate)
{
    internal EditorAuthoringState Owner { get; } = owner;
    internal Asset Asset { get; } = asset;
    internal AssetPropertyGraph Graph { get; } = graph;
    internal bool IsCurrent => Owner.IsPublished && !Owner.IsDisposed && ReferenceEquals(Owner.Registry.Find(Asset.Id), Owner);
    internal bool CanEdit(IGraphNode node) => IsCurrent && canEdit(node);
    internal bool CanEditItem(IAssetObjectNode collection, NodeIndex index) => IsCurrent && canEditItem(collection, index);
    internal void Mutate(Action action, string title)
    {
        if (!IsCurrent) throw new InvalidOperationException("The curve owner changed.");
        mutate(action, title);
    }
}
