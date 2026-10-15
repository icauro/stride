using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Annotations;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal PropertyNumericRange? FontItemNumericRange(INodePresenter presenter) => ResolveExtensionPresentation(presenter)?.NumericRange(presenter);
    internal IReadOnlyList<PropertyNumericRange?> FontItemAxisRanges(INodePresenter presenter) => ResolveExtensionPresentation(presenter)?.NumericAxisRanges(presenter, []) ?? [];
    internal bool FontItemAllowsNull(FontSettingField member, INodePresenter presenter)
    {
        var rules = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(member.Node.MemberDescriptor.MemberInfo)
            ?? member.Collection?.Descriptor.Attributes.OfType<MemberCollectionAttribute>().FirstOrDefault();
        return rules?.NotNullItems != true && (!presenter.Type.IsValueType || Nullable.GetUnderlyingType(presenter.Type) != null);
    }
    internal bool IsCurrentFontItemProperty(FontSettingField member, ItemId id, NodeIndex originalIndex, INodePresenter presenter)
    {
        if (!IsCurrentFontSettingField(member) || member.Collection is not { } collection) return false;
        if (id != ItemId.Empty && !collection.HasId(id)) return false;
        var index = id == ItemId.Empty ? originalIndex : collection.IdToIndex(id);
        var accessor = presenter.GetNodeAccessor();
        return collection.Indices?.Contains(index) == true && ReferenceEquals(accessor.Node, collection) && accessor.Index == index
            && ReferenceEquals(FindExtensionItemPresenter(collection, index), presenter);
    }
    internal bool CanEditFontItemProperty(FontSettingField member, ItemId id, NodeIndex index, INodePresenter presenter)
    {
        if (!IsCurrentFontItemProperty(member, id, index, presenter) || !CanEditFontSettings) return false;
        for (var current = presenter; current != null; current = current.Parent) if (current.IsReadOnly) return false;
        return presenter.GetNodeAccessor().Node is IAssetObjectNode collection && CanEditAssetSettingItem(collection, presenter.GetNodeAccessor().Index);
    }
    internal void UpdateFontItemProperty(FontSettingField member, ItemId id, NodeIndex index, INodePresenter presenter, object? value)
    {
        if (!CanEditFontItemProperty(member, id, index, presenter)) throw new InvalidOperationException("The font item is read-only or changed.");
        var rules = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(member.Node.MemberDescriptor.MemberInfo)
            ?? member.Collection?.Descriptor.Attributes.OfType<MemberCollectionAttribute>().FirstOrDefault();
        if (value == null ? rules?.NotNullItems == true || presenter.Type.IsValueType && Nullable.GetUnderlyingType(presenter.Type) == null
            : !(Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type).IsInstanceOfType(value))
            throw new ArgumentException("The font item value has an incompatible type.");
        NativeNumericAxes.AssertEditable(presenter, presenter.Value, value);
        value = PropertyValueValidation.Normalize(value, ResolveExtensionPresentation(presenter)?.NumericRange(presenter));
        value = NativeNumericAxes.Normalize(value, presenter.Value, ResolveExtensionPresentation(presenter)?.NumericAxisRanges(presenter, []) ?? []);
        ApplyInheritedAssetMutation(() => presenter.UpdateValue(value!), "Edit font item");
    }
}
