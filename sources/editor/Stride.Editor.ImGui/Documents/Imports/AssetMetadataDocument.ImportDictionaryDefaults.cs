using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal void AddDefaultImportCollectionItem(AssetImportSettingField field)
    {
        CommitEdits();
        if (!CanAddImportCollection(field) || field.Collection?.Descriptor is DictionaryDescriptor
            || !AssetCollectionCommandRules.CanAdd(field.ElementType!)) throw new InvalidOperationException("The collection command is unavailable.");
        var instance = AssetCollectionCommandRules.CreateItemInstance(field.ElementType!);
        var parent = importSettingsItems.FirstOrDefault(item => ReferenceEquals(item.NestedCollection?.Collection, field.Collection));
        var accessor = parent != null ? new NodeAccessor(parent.Collection, parent.Index) : new NodeAccessor(field.Node, NodeIndex.Empty);
        var value = AssetCollectionCommandRules.IsReferenceType(field.ElementType!) || importSettingsGraph!.Definition.IsObjectReference(accessor, instance)
            ? field.ElementType!.Default() : instance;
        if (value == null && field.CollectionRules?.NotNullItems == true) throw new InvalidOperationException("This collection cannot contain null.");
        if (!CanAddImportCollection(field)) throw new InvalidOperationException("The collection changed during item construction.");
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        var index = field.Collection!.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((field.Collection.Indices ?? []).Count());
        ApplyInheritedAssetMutation(() => field.Collection.Add(value!, index), "Add " + field.Label);
    }
    internal bool CanAddImportDictionaryKey(AssetImportSettingField field)
        => CanAddImportCollection(field) && field.Collection?.Descriptor is DictionaryDescriptor descriptor
        && AssetCollectionCommandRules.CanConstruct(descriptor.KeyType) && AssetCollectionCommandRules.CanAdd(descriptor.ValueType);

    internal void AddDefaultImportDictionaryKey(AssetImportSettingField field, object? parameter)
    {
        if (!CanAddImportDictionaryKey(field)) throw new InvalidOperationException("The dictionary command is unavailable.");
        var collection = field.Collection!; var descriptor = (DictionaryDescriptor)collection.Descriptor;
        var converter = System.ComponentModel.TypeDescriptor.GetConverter(descriptor.KeyType);
        var key = parameter != null && converter.CanConvertFrom(parameter.GetType()) ? converter.ConvertFrom(parameter)
            : descriptor.KeyType.IsEnum ? parameter : Activator.CreateInstance(descriptor.KeyType);
        if (key == null || !descriptor.KeyType.IsInstanceOfType(key)) throw new ArgumentException("The dictionary key is incompatible.");
        if (descriptor.ContainsKey(collection.Retrieve()!, key)) return;
        var instance = AssetCollectionCommandRules.CreateItemInstance(descriptor.ValueType);
        var parent = importSettingsItems.FirstOrDefault(item => ReferenceEquals(item.NestedCollection?.Collection, collection));
        var accessor = parent != null ? new NodeAccessor(parent.Collection, parent.Index) : new NodeAccessor(field.Node, NodeIndex.Empty);
        var value = AssetCollectionCommandRules.IsReferenceType(descriptor.ValueType) || importSettingsGraph!.Definition.IsObjectReference(accessor, instance)
            ? descriptor.ValueType.Default() : instance;
        if (value == null && field.CollectionRules?.NotNullItems == true) throw new InvalidOperationException("This collection cannot contain null.");
        if (!CanAddImportDictionaryKey(field)) throw new InvalidOperationException("The dictionary changed during item construction.");
        if (!descriptor.ContainsKey(collection.Retrieve()!, key))
            ApplyInheritedAssetMutation(() => collection.Add(value!, new NodeIndex(key)), "Add " + field.Label);
    }
}
