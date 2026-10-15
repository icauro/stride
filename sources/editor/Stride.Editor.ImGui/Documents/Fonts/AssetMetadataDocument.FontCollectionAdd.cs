using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Annotations;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IReadOnlyList<AbstractNodeEntry>? FontCollectionAddEntries(FontSettingField member)
        => IsCurrentFontSettingField(member) && member.Collection is { } collection
            ? ExtensionCollectionAbstractEntries(collection)?.ToArray() : null;
    internal void AddFontSettingItem(FontSettingField member, AbstractNodeEntry? entry)
    {
        EnsureFontSettingsGraph(); CommitEdits();
        if (!CanAddFontItem(member) || member.Collection is not { Descriptor: CollectionDescriptor descriptor } collection)
            throw new InvalidOperationException("This font collection cannot be edited.");
        var entries = FontCollectionAddEntries(member);
        if (entry == null ? entries != null : entries == null || !NativeAbstractEntries.Contains(entries, entry))
            throw new InvalidOperationException("The font item factory changed.");
        var value = entry == null ? AssetCollectionCommandRules.CreateItemInstance(descriptor.ElementType) : entry.GenerateValue(null!);
        var insertion = new NodeIndex((collection.Indices ?? []).Count());
        if (entry == null && (AssetCollectionCommandRules.IsReferenceType(descriptor.ElementType)
            || value != null && AuthoringGraph.Definition.IsTargetItemObjectReference(collection, insertion, value))) value = null;
        var rules = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(member.Node.MemberDescriptor.MemberInfo)
            ?? collection.Descriptor.Attributes.OfType<MemberCollectionAttribute>().FirstOrDefault();
        if (value == null ? rules?.NotNullItems == true || descriptor.ElementType.IsValueType && Nullable.GetUnderlyingType(descriptor.ElementType) == null
            : !(Nullable.GetUnderlyingType(descriptor.ElementType) ?? descriptor.ElementType).IsInstanceOfType(value))
            throw new ArgumentException("The font item factory returned an incompatible value.");
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        if (!CanAddFontItem(member) || !ReferenceEquals(member.Collection, collection)) throw new InvalidOperationException("The font collection changed.");
        ApplyInheritedAssetMutation(() => collection.Add(value, insertion), "Add font item");
    }
}
