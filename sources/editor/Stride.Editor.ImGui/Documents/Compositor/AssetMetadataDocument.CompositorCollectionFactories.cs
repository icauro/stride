using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IReadOnlyList<AbstractNodeEntry>? CompositorCollectionNativeEntries(CompositorProperty property)
        => property.Collection == null ? null : (CompositorPresenterEntries(property) ?? ExtensionCollectionAbstractEntries(property.Collection))?.ToArray();
    internal bool CanNativeCompositorCollectionAdd(CompositorProperty property)
        => CanAddCompositorPropertyItems(property) && property.Collection?.Descriptor is CollectionDescriptor descriptor
            && AssetCollectionCommandRules.CanAdd(descriptor.ElementType) && !CompositorPresenterReadOnly(property);
    internal void AddNativeCompositorCollectionItems(IReadOnlyList<CompositorProperty> properties, AbstractNodeEntry? entry)
    {
        CommitEdits();
        var plans = properties.DistinctBy(property => property.Collection).Select(property =>
        {
            var entries = CompositorCollectionNativeEntries(property);
            if (!CanNativeCompositorCollectionAdd(property) || (entry == null ? entries != null : entries == null || !NativeAbstractEntries.Contains(entries, entry)))
                throw new InvalidOperationException("The native compositor collection command or entry changed.");
            var node = property.Collection!; var type = ((CollectionDescriptor)node.Descriptor).ElementType;
            object? value;
            if (entry != null) value = entry.GenerateValue(null!);
            else
            {
                var instance = ObjectFactoryRegistry.NewInstance(type);
                value = AssetCollectionCommandRules.IsReferenceType(type) || AuthoringGraph.Definition.IsObjectReference(property.Accessor, instance) ? null : instance;
            }
            if (value == null ? type.IsValueType && Nullable.GetUnderlyingType(type) == null || property.Rules?.NotNullItems == true
                : !(Nullable.GetUnderlyingType(type) ?? type).IsInstanceOfType(value))
                throw new ArgumentException("The native collection entry has an incompatible value.");
            if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
            return (Property: property, Node: node, Value: value);
        }).Where(plan => plan.Node.Descriptor is not SetDescriptor set || !set.Contains(plan.Node.Retrieve()!, plan.Value)).ToArray();
        if (plans.Length == 0) return;
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var plan in plans)
            {
                if (!CanNativeCompositorCollectionAdd(plan.Property)) throw new InvalidOperationException("The compositor collection target changed before insertion.");
                // Quantum owns actual list/set insertion and native item identity.
                plan.Node.Add(plan.Value!);
            }
        }, "Add " + properties[0].Label);
    }
}
