using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    internal bool CanCopyProperty(IMemberNode member)
        => member is IAssetNode node && authoringNodes.Contains(node);
    private NativePropertyClipboard PropertyClipboard()
        => new(AuthoringGraph, Asset,
            member => CanEditAssetSettingNode(member) && !NumericReadOnly(member) && !PropertyClipboardRules.IsReadOnly(member),
            CanEditNativeCollectionItem, CanAddNativeCollection, CanRemoveNativeCollection,
            normalizeMember: NormalizeClipboardProperty, normalizeItem: NormalizeNativeClipboardItem,
            afterMemberUpdate: (_, _) => RefreshFields(),
            acceptItem: (collection, _, value) => value != null || !NativeCollectionNotNullItems(collection));
    private object NormalizeClipboardProperty(IMemberNode member, object value)
    {
        var previous = member.Retrieve();
        var normalized = NativeNumericAxes.Normalize(PropertyValueValidation.Normalize(value, NumericRange(member)), previous, AxisNumericRanges(member))!;
        if (NumericPresentation?.Find(member) is { } presenter) NativeNumericAxes.AssertEditable(presenter, previous, normalized);
        return normalized;
    }
    internal string CopyProperty(IMemberNode member)
    {
        EndEdit();
        if (!CanCopyProperty(member)) throw new InvalidOperationException("The property has no canonical authored owner.");
        return PropertyClipboard().Copy(new(member, NodeIndex.Empty));
    }
    internal bool CanPasteProperty(IMemberNode member, string text)
        => CanCopyProperty(member)
            && !PropertyClipboardRules.IsReadOnly(member)
            && (member.Target is { IsEnumerable: true } collection ? CanEditNativeCollection(collection)
                : CanEditAssetSettingNode(member) && !NumericReadOnly(member))
            && PropertyClipboard().CanPaste(text, member.Type);
    internal void PasteProperty(IMemberNode member, string text, bool replace)
    {
        EndEdit();
        if (!CanPasteProperty(member, text)) throw new InvalidOperationException("The clipboard cannot be pasted into this procedural property.");
        var clipboard = PropertyClipboard();
        ApplyInheritedAssetMutation(() =>
        {
            clipboard.Paste(text, member.Type, new(member, NodeIndex.Empty), replace);
            var field = new AssetPropertyField(member, member.Name);
            if (member.Retrieve() == null && !field.AllowsNull)
                throw new InvalidOperationException("The procedural property cannot be null.");
        }, replace ? "Replace property" : "Paste property");
    }
    internal string CopyCollectionItem(ProceduralCollectionItem item)
    {
        EndEdit();
        if (!IsCollectionItemCurrent(item)) throw new InvalidOperationException("The procedural item is no longer current.");
        return PropertyClipboard().Copy(new(item.Context.Node, item.Index));
    }
    internal bool CanPasteCollectionItem(ProceduralCollectionItem item, string text)
        => CanEditCollectionItem(item) && PropertyClipboard().CanPaste(text, item.Context.Node.Type);
    internal void PasteCollectionItem(ProceduralCollectionItem item, string text, bool replace)
    {
        EndEdit();
        if (!CanPasteCollectionItem(item, text)) throw new InvalidOperationException("The clipboard cannot be pasted into this procedural item.");
        var clipboard = PropertyClipboard();
        ApplyInheritedAssetMutation(() => clipboard.Paste(text, item.Context.Node.Type, new(item.Context.Node, item.Index), replace),
            replace ? "Replace item" : "Paste item");
    }
}
