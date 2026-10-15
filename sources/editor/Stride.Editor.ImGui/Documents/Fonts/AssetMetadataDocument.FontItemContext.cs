using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private bool TryGetFontItemAccessor(FontSettingField member, FontSettingItem item, out NodeAccessor accessor)
    {
        accessor = default;
        EnsureFontSettingsGraph();
        if (!fontFields.Any(current => ReferenceEquals(current.Node, member.Node) && current.Key == member.Key)
            || !CanCopyAssetProperty(member.Node) || member.Collection is not { } collection) return false;
        if (item.Id != ItemId.Empty && !collection.HasId(item.Id)) return false;
        var index = item.Id == ItemId.Empty ? item.Index : collection.IdToIndex(item.Id);
        if (collection.Indices?.Contains(index) != true || !ContentAssetReference.AreEqual(collection.Retrieve(index), item.Value)) return false;
        accessor = new(collection, index);
        return true;
    }
    internal bool CanCopyFontItem(FontSettingField member, FontSettingItem item) => TryGetFontItemAccessor(member, item, out _);
    internal string CopyFontItem(FontSettingField member, FontSettingItem item)
    {
        CommitEdits();
        if (!TryGetFontItemAccessor(member, item, out var accessor)) throw new InvalidOperationException("The authored font item is no longer available.");
        return CopyAssetPropertyAccessor(accessor);
    }
    internal bool CanPasteFontItem(FontSettingField member, FontSettingItem item, string text)
        => TryGetFontItemAccessor(member, item, out var accessor) && CanPasteAssetAccessors([accessor], text);
    internal void PasteFontItem(FontSettingField member, FontSettingItem item, string text, bool replace)
    {
        CommitEdits();
        if (!TryGetFontItemAccessor(member, item, out var accessor) || !CanPasteAssetAccessors([accessor], text))
            throw new InvalidOperationException("The clipboard cannot be pasted into this font item.");
        PasteAssetAccessors([accessor], text, replace);
    }
    internal bool CanResetFontItem(FontSettingField member, FontSettingItem item)
        => TryGetFontItemAccessor(member, item, out var accessor) && CanResetAssetSettingItem((IAssetObjectNode)accessor.Node, accessor.Index);
    internal void ResetFontItem(FontSettingField member, FontSettingItem item)
    {
        CommitEdits();
        if (!TryGetFontItemAccessor(member, item, out var accessor)) throw new InvalidOperationException("The authored font item is no longer available.");
        ResetAssetSettingItem((IAssetObjectNode)accessor.Node, accessor.Index);
    }
    internal EditorPresenterTarget? AcquireFontItemCommandTarget(FontSettingField member, FontSettingItem item)
        => TryGetFontItemAccessor(member, item, out var accessor) ? AcquireExtensionFactoryTarget(accessor) : null;
    internal bool IsFontItemCommandTargetCurrent(FontSettingField member, FontSettingItem item, EditorPresenterTarget target)
    {
        if (!TryGetFontItemAccessor(member, item, out var accessor)) return false;
        var actual = target.Presenter.GetNodeAccessor();
        return ReferenceEquals(actual.Node, accessor.Node) && actual.Index == accessor.Index;
    }
    internal bool CanAddFontItem(FontSettingField member)
        => CanEditFontSettings && CanCopyAssetProperty(member.Node) && !IsExtensionPropertyReadOnly(member.Node)
            && !PropertyClipboardRules.IsReadOnly(member.Node) && member.Collection is { } collection
            && CanEditAssetSettingNode(collection) && NativePropertyClipboard.HasAdd(collection)
            && collection.Descriptor is CollectionDescriptor descriptor && Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(descriptor.ElementType);
    internal bool CanRemoveFontItem(FontSettingField member, FontSettingItem item)
        => TryGetFontItemAccessor(member, item, out var accessor) && CanEditFontSettings
            && !IsExtensionPropertyReadOnly(member.Node) && !PropertyClipboardRules.IsReadOnly(member.Node)
            && CanEditAssetSettingItem((IAssetObjectNode)accessor.Node, accessor.Index) && NativePropertyClipboard.HasRemove((IObjectNode)accessor.Node)
            && accessor.Node.Descriptor is CollectionDescriptor descriptor && Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(descriptor.ElementType);
}
