using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Native presenter factory with virtual properties bound to actual authored accessors.</summary>
public class EditorNodePresenterFactory(INodeBuilder builder, IReadOnlyCollection<INodePresenterCommand> commands, IReadOnlyCollection<INodePresenterUpdater> updaters)
    : NodePresenterFactory(builder, commands, updaters)
{
    public VirtualNodePresenter CreateVirtualNode(INodePresenter parent, string name, Type type, NodeAccessor associated,
        Func<NodeAccessor, object> getter, Action<NodeAccessor, object>? setter = null, int? order = null, Action<VirtualNodePresenter>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(parent); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(type); ArgumentNullException.ThrowIfNull(getter);
        if (setter != null && associated.Node == null) throw new ArgumentException("An editable virtual property requires an authored node accessor.", nameof(associated));
        var itemId = associated.Node is IAssetObjectNode collection && !associated.Index.IsEmpty && collection.TryIndexToId(associated.Index, out var captured)
            ? captured : default;
        NodeAccessor Resolve()
        {
            if (itemId == default) return associated;
            var collection = (IAssetObjectNode)associated.Node;
            if (!collection.HasId(itemId)) throw new InvalidOperationException("The virtual property's authored item no longer exists.");
            return new(collection, collection.IdToIndex(itemId));
        }
        var node = new BoundVirtualNode(this, parent.PropertyProvider!, parent, name, type, order,
            () => getter(Resolve()), setter == null ? null : value => setter(Resolve(), value), Resolve);
        if (associated.Node != null) node.RegisterAssociatedNode(associated);
        node.IsReadOnly = setter == null; node.ChangeParent(parent);
        configure?.Invoke(node);
        node.Commands.Clear(); node.Commands.AddRange(AvailableCommands.Where(command => command.CanAttach(node)));
        RunUpdaters(node); FinalizeTree(node.Root);
        return node;
    }
    private sealed class BoundVirtualNode(INodePresenterFactoryInternal factory, IPropertyProviderViewModel provider, INodePresenter parent,
        string name, Type type, int? order, Func<object> getter, Action<object>? setter, Func<NodeAccessor> accessor)
        : VirtualNodePresenter(factory, provider, parent, name, type, order, getter, setter!)
    {
        public override NodeIndex Index
        {
            get { try { return accessor().Index; } catch (InvalidOperationException) { return AssociatedNode.Index; } }
        }
        public override NodeAccessor GetNodeAccessor() => accessor();
    }
}
