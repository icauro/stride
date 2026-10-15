using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal IReadOnlyList<AbstractNodeEntry>? CompositorValueNativeEntries(CompositorProperty property)
        => (CompositorPresenterEntries(property) ?? (property.Accessor.IsItem ? ExtensionAbstractEntries((IObjectNode)property.Accessor.Node, property.Accessor.Index)
                ?? (property.ParentCollection is { } parent ? CompositorPresenterEntries(parent) : null)
                ?? ExtensionCollectionAbstractEntries((IObjectNode)property.Accessor.Node)
            : property.Accessor.Node is IMemberNode member ? ExtensionAbstractEntries(member) : null))?.OrderBy(entry => entry.Order).ToArray();
    internal IReadOnlyList<AbstractNodeEntry>? CommonCompositorValueNativeEntries(IReadOnlyList<CompositorProperty> fields)
    {
        var entries = fields.Select(CompositorValueNativeEntries).Where(entries => entries != null).Select(entries => entries!).ToArray();
        return entries.Length == 0 ? null : NativeAbstractEntries.Combine(entries);
    }
    internal bool CanSelectCompositorValueEntry(IReadOnlyList<CompositorProperty> fields, AbstractNodeEntry entry)
        => fields.Count != 0 && fields.All(field => CanEditCompositorProperty(field)
            && NativeAbstractEntries.Accepts(CompositorValueNativeEntries(field), entry))
            && CommonCompositorValueNativeEntries(fields) is { } entries && NativeAbstractEntries.Contains(entries, entry);
    internal void SelectCompositorValueEntry(IReadOnlyList<CompositorProperty> fields, AbstractNodeEntry entry, CompositorAggregateProperty? aggregate)
    {
        CommitEdits();
        if (!CanSelectCompositorValueEntry(fields, entry)) throw new InvalidOperationException("The native compositor value entry changed.");
        var values = new Dictionary<NodeAccessor, object?>();
        foreach (var field in fields)
            if (!values.ContainsKey(field.Accessor))
            {
                var current = CompositorPropertyValue(field);
                var value = entry.IsMatchingValue(current!) ? current : entry.GenerateValue(current!);
                if (!ReferenceEquals(value, current) && value != null && !AuthoringGraph.Definition.IsObjectReference(field.Accessor, value))
                    AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
                values.Add(field.Accessor, value);
            }
        if (fields.All(field => Equals(CompositorPropertyValue(field), values[field.Accessor]))) return;
        if (aggregate != null) EditCompositorAggregate(aggregate, field => values[field.Accessor]);
        else { UpdateCompositorProperty(fields[0], values[fields[0].Accessor]); EndCompositorSettingEdit(); }
    }
}
