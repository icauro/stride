using System.ComponentModel;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal Action PrepareHierarchyDictionaryDefaultAdd(HierarchyCollectionContext context, object? parameter)
    {
        if (!CanAddNestedHierarchyKeys(context) || context.Descriptor is not DictionaryDescriptor descriptor
            || !AssetCollectionCommandRules.CanConstruct(descriptor.KeyType) || !AssetCollectionCommandRules.CanAdd(descriptor.ValueType))
            throw new InvalidOperationException("The dictionary command is unavailable.");
        var converter = TypeDescriptor.GetConverter(descriptor.KeyType);
        var key = parameter != null && converter.CanConvertFrom(parameter.GetType()) ? converter.ConvertFrom(parameter)
            : descriptor.KeyType.IsEnum ? parameter : Activator.CreateInstance(descriptor.KeyType);
        if (key == null || !descriptor.KeyType.IsInstanceOfType(key)) throw new ArgumentException("The dictionary key is incompatible.");
        if (descriptor.ContainsKey(context.Node.Retrieve()!, key)) return () => { };
        var instance = AssetCollectionCommandRules.CreateItemInstance(descriptor.ValueType);
        var accessor = context.Parent is { } parent ? new NodeAccessor(parent.Collection, parent.Index) : new NodeAccessor(context.Origin.Node, NodeIndex.Empty);
        var value = Stride.Core.Assets.AssetCollectionCommandRules.IsReferenceType(descriptor.ValueType) || HierarchyGraph.Definition.IsObjectReference(accessor, instance)
            ? descriptor.ValueType.IsValueType ? Activator.CreateInstance(descriptor.ValueType) : null : instance;
        ValidateNestedHierarchyValue(context, value);
        return () =>
        {
            if (!CanAddNestedHierarchyKeys(context)) throw new InvalidOperationException("The dictionary changed before insertion.");
            if (!descriptor.ContainsKey(context.Node.Retrieve()!, key)) context.Node.Add(value!, new NodeIndex(key));
        };
    }
    internal void AddHierarchyDictionaryDefaultItem(HierarchyCollectionContext context, object? parameter)
    { EndEdits(); var change = PrepareHierarchyDictionaryDefaultAdd(context, parameter); ApplyInheritedGraphMutation(change, "Add key: " + context.Origin.Label); }
}
