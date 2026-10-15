using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal static bool VirtualClipboardUsesSetter(VirtualNodePresenter presenter)
    {
        var accessor = presenter.GetNodeAccessor();
        var valueType = accessor.IsMember ? accessor.Node?.Type : accessor.Node?.Descriptor is DictionaryDescriptor dictionary
            ? dictionary.ValueType : accessor.Node?.Descriptor is CollectionDescriptor collection ? collection.ElementType : accessor.Node?.Type;
        return valueType != presenter.Type;
    }
    internal bool CanCopyVirtualProperty(VirtualNodePresenter presenter)
    {
        if (!ExtensionVirtualProperties.Contains(presenter)) return false;
        var accessor = presenter.GetNodeAccessor();
        _ = AuthoringGraph;
        return accessor.Node == null || accessor.Node is IAssetNode node && authoringNodes.Contains(node)
            && (accessor.IsMember || accessor.Node is IObjectNode collection && (collection.Indices ?? []).Contains(accessor.Index));
    }
    internal string CopyVirtualProperty(VirtualNodePresenter presenter)
    {
        using var target = AcquireExtensionVirtualTarget(presenter);
        CommitEdits();
        return CanCopyVirtualProperty(presenter) ? CreateAssetPropertyClipboard().CopyValue(presenter.Value, presenter.GetNodeAccessor()) : "";
    }
    internal bool CanPasteVirtualProperty(VirtualNodePresenter presenter, string text)
    {
        if (!CanCopyVirtualProperty(presenter)) return false;
        for (INodePresenter? current = presenter; current != null; current = current.Parent) if (current.IsReadOnly) return false;
        var accessor = presenter.GetNodeAccessor();
        if (accessor.Node is IMemberNode member)
        {
            if (!CanEditAssetSettingNode(member) || IsExtensionPropertyReadOnly(member) || PropertyClipboardRules.IsReadOnly(member)
                || member.Target?.IsEnumerable != true && !CanAssignAuthoringNode(member)) return false;
        }
        else if (accessor.Node is not IAssetObjectNode collection || !CanMutateClipboardCollection(collection)
            || !CanEditAssetSettingItem(collection, accessor.Index)) return false;
        var projection = VirtualClipboardUsesSetter(presenter);
        return (!projection || !CollectionDescriptor.IsCollection(presenter.Type))
            && CreateAssetPropertyClipboard().CanPaste(text, projection ? presenter.Type : accessor.Node.Type);
    }
    internal void PasteVirtualProperty(VirtualNodePresenter presenter, string text, bool replace)
    {
        using var target = AcquireExtensionVirtualTarget(presenter);
        CommitEdits();
        if (!CanPasteVirtualProperty(presenter, text)) throw new InvalidOperationException("The clipboard cannot be pasted into this authored virtual property.");
        var accessor = presenter.GetNodeAccessor();
        var clipboard = CreateAssetPropertyClipboard();
        if (VirtualClipboardUsesSetter(presenter))
        {
            var value = clipboard.PrepareValue(text, presenter.Type);
            UpdateExtensionVirtualProperty(presenter, value);
        }
        else ApplyInheritedAssetMutation(() => clipboard.Paste(text, accessor.Node.Type, presenter.GetNodeAccessor(), replace),
            replace ? "Replace property" : "Paste property");
    }
}
