using System.ComponentModel;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    internal void AddDefaultDictionaryItem(ProceduralCollectionContext context, object? parameter)
    {
        if (!CanAddCollection(context) || context.Node.Descriptor is not DictionaryDescriptor descriptor
            || !AssetCollectionCommandRules.CanConstruct(descriptor.KeyType) || !AssetCollectionCommandRules.CanAdd(descriptor.ValueType))
            throw new InvalidOperationException("The dictionary command is unavailable.");
        var converter = TypeDescriptor.GetConverter(descriptor.KeyType);
        var key = parameter != null && converter.CanConvertFrom(parameter.GetType()) ? converter.ConvertFrom(parameter)
            : descriptor.KeyType.IsEnum ? parameter : Activator.CreateInstance(descriptor.KeyType);
        if (key == null || !descriptor.KeyType.IsInstanceOfType(key)) throw new ArgumentException("The dictionary key is incompatible.");
        if (descriptor.ContainsKey(context.Node.Retrieve()!, key)) return;
        var instance = AssetCollectionCommandRules.CreateItemInstance(descriptor.ValueType);
        var accessor = context.Parent is { } parent ? new NodeAccessor(parent.Context.Node, parent.Index)
            : new NodeAccessor(context.Origin.Node, NodeIndex.Empty);
        var value = AssetCollectionCommandRules.IsReferenceType(descriptor.ValueType) || AuthoringGraph.Definition.IsObjectReference(accessor, instance)
            ? descriptor.ValueType.Default() : instance;
        if (!CanAddCollection(context)) throw new InvalidOperationException("The dictionary changed during item construction.");
        if (!descriptor.ContainsKey(context.Node.Retrieve()!, key)) AddCollectionItem(context, value, key);
    }
}
