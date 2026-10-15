using System.ComponentModel;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool CanAddNativeCompositorDictionaryKey(CompositorProperty property)
        => CanAddCompositorPropertyItems(property) && !CompositorPresenterReadOnly(property)
            && property.Collection?.Descriptor is DictionaryDescriptor descriptor
            && AssetCollectionCommandRules.CanConstruct(descriptor.KeyType) && AssetCollectionCommandRules.CanAdd(descriptor.ValueType);
    internal void AddNativeCompositorDictionaryKeys(IReadOnlyList<CompositorProperty> properties, object? parameter)
    {
        CommitEdits();
        var plans = new List<(CompositorProperty Property, DictionaryDescriptor Descriptor, object Key, object? Value)>();
        foreach (var property in properties.DistinctBy(property => property.Collection))
        {
            if (!CanAddNativeCompositorDictionaryKey(property) || property.Collection?.Descriptor is not DictionaryDescriptor descriptor)
                throw new InvalidOperationException("The native compositor dictionary command is unavailable.");
            var converter = TypeDescriptor.GetConverter(descriptor.KeyType);
            var key = parameter != null && converter.CanConvertFrom(parameter.GetType()) ? converter.ConvertFrom(parameter)
                : descriptor.KeyType.IsEnum ? parameter : Activator.CreateInstance(descriptor.KeyType);
            if (key == null || !descriptor.KeyType.IsInstanceOfType(key)) throw new ArgumentException("The dictionary key is incompatible.");
            if (descriptor.ContainsKey(property.Collection.Retrieve()!, key)) continue;
            var instance = AssetCollectionCommandRules.CreateItemInstance(descriptor.ValueType);
            var value = Stride.Core.Assets.AssetCollectionCommandRules.IsReferenceType(descriptor.ValueType) || AuthoringGraph.Definition.IsObjectReference(property.Accessor, instance)
                ? descriptor.ValueType.IsValueType ? Activator.CreateInstance(descriptor.ValueType) : null : instance;
            if (value == null ? descriptor.ValueType.IsValueType && Nullable.GetUnderlyingType(descriptor.ValueType) == null || property.Rules?.NotNullItems == true
                : !(Nullable.GetUnderlyingType(descriptor.ValueType) ?? descriptor.ValueType).IsInstanceOfType(value))
                throw new ArgumentException("The native dictionary value is incompatible.");
            if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
            plans.Add((property, descriptor, key, value));
        }
        if (plans.Count == 0) return;
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var plan in plans)
            {
                if (!CanAddNativeCompositorDictionaryKey(plan.Property)) throw new InvalidOperationException("The compositor dictionary changed before insertion.");
                var collection = plan.Property.Collection!;
                if (!plan.Descriptor.ContainsKey(collection.Retrieve()!, plan.Key)) collection.Add(plan.Value!, new NodeIndex(plan.Key));
            }
        }, "Add " + properties[0].Label);
    }
}
