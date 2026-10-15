using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanCopyComponentVirtual(VirtualNodePresenter presenter)
    {
        var presentation = propertyPresenters.FirstOrDefault(value => value.OwnsVirtual(presenter));
        return presentation?.AuthoredValue is EntityComponent component && component.Entity is { } entity && IsSelected(entity.Id)
            && Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) && ReferenceEquals(design.Entity, entity)
            && entity.Components.Contains(component);
    }
    private NativePropertyClipboard ComponentVirtualClipboard(VirtualNodePresenter presenter)
    {
        var presentation = propertyPresenters.Single(value => value.OwnsVirtual(presenter));
        return CreatePropertyClipboard(GetComponentFields((EntityComponent)presentation.AuthoredValue));
    }
    internal string CopyComponentVirtual(VirtualNodePresenter presenter)
    {
        EndEdits();
        if (!CanCopyComponentVirtual(presenter)) throw new InvalidOperationException("The component property is no longer selected.");
        return ComponentVirtualClipboard(presenter).CopyValue(presenter.Value, presenter.GetNodeAccessor());
    }
    internal bool CanPasteComponentVirtual(VirtualNodePresenter presenter, string text)
    {
        if (!CanCopyComponentVirtual(presenter)) return false;
        for (INodePresenter? current = presenter; current != null; current = current.Parent) if (current.IsReadOnly) return false;
        var accessor = presenter.GetNodeAccessor();
        if (accessor.Node is IMemberNode member)
        {
            if (!CanEditHierarchyNode(member) || PropertyClipboardRules.IsReadOnly(member)
                || member.Target?.IsEnumerable != true && !AssetMetadataDocument.CanAssignAuthoringNode(member)) return false;
        }
        else if (accessor.Node is not IAssetObjectNode collection || !CanEditHierarchyItem(collection, accessor.Index)) return false;
        var projected = AssetMetadataDocument.VirtualClipboardUsesSetter(presenter);
        return (!projected || !CollectionDescriptor.IsCollection(presenter.Type))
            && ComponentVirtualClipboard(presenter).CanPaste(text, projected ? presenter.Type : accessor.Node.Type);
    }
    internal void PasteComponentVirtual(IReadOnlyList<VirtualNodePresenter> presenters, string text, bool replace)
    {
        EndEdits();
        if (presenters.Count == 0 || presenters.Any(node => !CanPasteComponentVirtual(node, text)))
            throw new InvalidOperationException("The clipboard cannot be pasted into these component properties.");
        var changes = presenters.Distinct().Select(node => (Node: node, Clipboard: ComponentVirtualClipboard(node),
            Projected: AssetMetadataDocument.VirtualClipboardUsesSetter(node))).ToArray();
        ApplyInheritedGraphMutation(() =>
        {
            foreach (var change in changes)
            {
                if (!CanPasteComponentVirtual(change.Node, text)) throw new InvalidOperationException("A component property became read-only.");
                if (change.Projected)
                    change.Node.UpdateValue(NormalizeComponentVirtualValue(change.Node, change.Clipboard.PrepareValue(text, change.Node.Type))!);
                else
                {
                    var accessor = change.Node.GetNodeAccessor();
                    change.Clipboard.Paste(text, accessor.Node.Type, accessor, replace);
                }
            }
        }, replace ? "Replace property" : "Paste property");
    }
    internal bool CanResetComponentVirtual(VirtualNodePresenter presenter)
    {
        if (!CanCopyComponentVirtual(presenter) || !CanEditComponentVirtual(presenter)) return false;
        var accessor = presenter.GetNodeAccessor();
        return accessor.Node is IAssetMemberNode member ? CanResetHierarchyNode(member)
            : accessor.Node is IAssetObjectNode collection && !accessor.Index.IsEmpty && CanResetHierarchyItem(collection, accessor.Index);
    }
    internal void ResetComponentVirtual(IReadOnlyList<VirtualNodePresenter> presenters)
    {
        EndEdits();
        if (presenters.Count == 0 || presenters.Any(node => !CanResetComponentVirtual(node)))
            throw new InvalidOperationException("These component properties have no editable base value.");
        ApplyInheritedGraphMutation(() =>
        {
            foreach (var node in presenters.Distinct())
            {
                if (!CanResetComponentVirtual(node)) throw new InvalidOperationException("A component property changed before resetting.");
                var accessor = node.GetNodeAccessor();
                if (accessor.Node is IAssetMemberNode member) member.ResetOverrideRecursively();
                else ((IAssetObjectNode)accessor.Node).ResetOverrideRecursively(accessor.Index);
            }
        }, "Reset to base value");
    }
}
