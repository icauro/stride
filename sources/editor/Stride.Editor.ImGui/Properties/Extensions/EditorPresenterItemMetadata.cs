using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Finds the actual authored item presenter; virtual aliases cannot redefine its item permissions.</summary>
internal static class EditorPresenterItemMetadata
{
    internal static INodePresenter? Find(INodePresenter root, IObjectNode collection, NodeIndex index)
    {
        var pending = new Stack<INodePresenter>(); pending.Push(root);
        while (pending.Count != 0)
        {
            var presenter = pending.Pop();
            if (presenter is not VirtualNodePresenter)
            {
                var accessor = presenter.GetNodeAccessor();
                if (ReferenceEquals(accessor.Node, collection) && accessor.Index == index) return presenter;
            }
            foreach (var child in presenter.Children) pending.Push(child);
        }
        return null;
    }
    internal static bool IsVisible(INodePresenter presenter)
    {
        for (INodePresenter? node = presenter; node != null; node = node.Parent)
            if (!node.IsVisible) return false;
        return true;
    }
    internal static INodePresenter? FindCollection(INodePresenter root, IObjectNode collection)
    {
        var pending = new Stack<INodePresenter>(); pending.Push(root);
        while (pending.Count != 0)
        {
            var presenter = pending.Pop(); var accessor = presenter.GetNodeAccessor();
            if (accessor.Node is IMemberNode member && ReferenceEquals(member.Target, collection)
                || accessor.Node is IObjectNode owner && (accessor.Index.IsEmpty ? ReferenceEquals(owner, collection)
                    : owner.ItemReferences != null && (owner.Indices ?? []).Contains(accessor.Index) && ReferenceEquals(owner.IndexedTarget(accessor.Index), collection))) return presenter;
            foreach (var child in presenter.Children) pending.Push(child);
        }
        return null;
    }
    internal static bool HasReadOnlyItemAncestor(INodePresenter presenter)
    {
        for (var parent = presenter.Parent; parent != null; parent = parent.Parent)
            if (parent is ItemNodePresenter && parent.IsReadOnly) return true;
        return false;
    }
    internal static bool IsReadOnly(INodePresenter presenter) => presenter.IsReadOnly;
}
