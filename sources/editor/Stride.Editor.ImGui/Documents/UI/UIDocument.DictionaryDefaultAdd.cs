using System.ComponentModel;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    internal bool CanAddUIDictionaryKey(UIPropertyCollection collection)
        => CanAddCollectionItem(collection) && collection.Collection.Descriptor is DictionaryDescriptor descriptor
            && AssetCollectionCommandRules.CanConstruct(descriptor.KeyType) && AssetCollectionCommandRules.CanAdd(descriptor.ValueType);

    private Action? PrepareUIDictionaryDefaultAdd(UIPropertyCollection collection, object? parameter)
    {
        if (!CanAddUIDictionaryKey(collection) || collection.Collection.Descriptor is not DictionaryDescriptor descriptor)
            throw new InvalidOperationException("The native UI dictionary command is unavailable.");
        var converter = TypeDescriptor.GetConverter(descriptor.KeyType);
        var key = parameter != null && converter.CanConvertFrom(parameter.GetType()) ? converter.ConvertFrom(parameter)
            : descriptor.KeyType.IsEnum ? parameter : Activator.CreateInstance(descriptor.KeyType);
        if (key == null || !descriptor.KeyType.IsInstanceOfType(key)) throw new ArgumentException("The dictionary key is incompatible.");
        if (descriptor.ContainsKey(collection.Collection.Retrieve()!, key)) return null;
        var instance = AssetCollectionCommandRules.CreateItemInstance(descriptor.ValueType);
        var value = Stride.Core.Assets.AssetCollectionCommandRules.IsReferenceType(descriptor.ValueType) || AuthoringGraph.Definition.IsObjectReference(collection.Accessor, instance)
            ? descriptor.ValueType.IsValueType ? Activator.CreateInstance(descriptor.ValueType) : null : instance;
        ValidateCollectionValue(collection, value);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        return () =>
        {
            if (!CanAddUIDictionaryKey(collection)) throw new InvalidOperationException("The native UI dictionary target changed.");
            if (!descriptor.ContainsKey(collection.Collection.Retrieve()!, key)) collection.Collection.Add(value, new NodeIndex(key));
        };
    }
    internal void AddUIDictionaryDefaultItems(IReadOnlyList<UIPropertyCollection> collections, object? parameter)
    {
        EndEdit();
        var changes = collections.DistinctBy(collection => collection.Collection).Select(collection => PrepareUIDictionaryDefaultAdd(collection, parameter)).OfType<Action>().ToArray();
        if (changes.Length == 0) return;
        BatchCollectionEdits(() =>
        {
            foreach (var change in changes) Mutate(change, "Add UI dictionary key");
        }, "Add UI dictionary key");
    }
}
