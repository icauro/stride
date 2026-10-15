using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    public void CreateCompositorAggregateObjects(CompositorAggregateProperty property, Type? type)
    {
        if (type != null && (IsExistingCompositorReferenceType(type) || property.Fields.Any(field => AssetRegistry.CanPropertyHandleContent(field.Type, out var contentTypes) && contentTypes.Contains(type)
            || type != field.Type && !field.Types.Contains(type))))
            throw new InvalidOperationException("The selected compositor object type cannot be created.");
        var values = new Dictionary<NodeAccessor, object?>();
        EditCompositorAggregate(property, field =>
        {
            if (!values.TryGetValue(field.Accessor, out var value))
            {
                value = type == null ? null : ObjectFactoryRegistry.NewInstance(type);
                AssetCollectionItemIdHelper.GenerateMissingItemIds(value); values.Add(field.Accessor, value);
            }
            return value;
        });
    }
    public bool CanResizeCompositorAggregate(CompositorAggregateProperty property)
    {
        foreach (var field in property.Fields) if (!CanResizeCompositorPropertyCollection(field)) return false;
        return property.Fields.Count > 0;
    }
    public bool CanAddCompositorAggregateItems(CompositorAggregateProperty property)
    {
        foreach (var field in property.Fields) if (!CanAddCompositorPropertyItems(field)) return false;
        return property.Fields.Count > 0;
    }
    public void AddCompositorAggregateItems(CompositorAggregateProperty property, Type? type, object? reference = null, string? keyText = null)
    {
        CommitEdits();
        if (!CanAddCompositorAggregateItems(property)) throw new InvalidOperationException("The selected compositor collections cannot be edited together.");
        var plans = new List<(IAssetObjectNode Node, NodeIndex Index, object? Value)>();
        var seen = new HashSet<IAssetObjectNode>(ReferenceEqualityComparer.Instance);
        foreach (var field in property.Fields)
        {
            var node = field.Collection!;
            if (!seen.Add(node)) continue;
            if (type != null && !field.ItemTypes.Contains(type)
                || type == null && reference == null && (field.Rules?.NotNullItems == true || node.Descriptor.GetInnerCollectionType().IsValueType && Nullable.GetUnderlyingType(node.Descriptor.GetInnerCollectionType()) == null))
                throw new InvalidOperationException("The selected compositor item cannot be added.");
            if (type == null) ValidateCompositorCollectionReference(field, reference);
            var value = type == null ? reference : ObjectFactoryRegistry.NewInstance(type);
            if (type != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
            var index = node.Descriptor is DictionaryDescriptor dictionary ? new NodeIndex(NativeDictionaryKey.CreateForAdd(dictionary.KeyType, keyText))
                : node.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex(node.Indices!.Count());
            if (node.Descriptor is DictionaryDescriptor map && map.ContainsKey(node.Retrieve(), index.Value)) throw new ArgumentException("The dictionary already contains that key.");
            plans.Add((node, index, value));
        }
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var plan in plans)
                if (plan.Node.Descriptor is DictionaryDescriptor) plan.Node.Add(plan.Value!, plan.Index);
                else plan.Node.Add(plan.Value!);
        }, "Add " + property.Label);
    }
    public bool CanRemoveCompositorAggregateItems(CompositorAggregateProperty item)
    {
        foreach (var field in item.Fields)
            if (!CanEditCompositorProperty(field) || field.ParentCollection == null || !CanRemoveCompositorPropertyItems(field.ParentCollection)) return false;
        return item.Fields.Count > 0;
    }
    public void RemoveCompositorAggregateItems(CompositorAggregateProperty item)
    {
        CommitEdits();
        if (!CanRemoveCompositorAggregateItems(item)) throw new InvalidOperationException("The selected compositor items cannot be removed together.");
        var fields = item.Fields.DistinctBy(field => field.Accessor).ToArray();
        ApplyInheritedAssetMutation(() => { foreach (var field in fields) ((IAssetObjectNode)field.Accessor.Node).Remove(field.Accessor.RetrieveValue(), field.Accessor.Index); }, "Remove " + item.Label);
    }
    public bool CanMoveCompositorAggregateItems(CompositorAggregateProperty item, int destination)
    {
        if (!CanRemoveCompositorAggregateItems(item)) return false;
        foreach (var field in item.Fields)
            if (field.ParentCollection!.Rules?.CanReorderItems != true || !field.Accessor.Index.IsInt
                || field.Accessor.Node.Descriptor is not CollectionDescriptor { HasInsert: true } || destination < 0 || destination >= ((IObjectNode)field.Accessor.Node).Indices!.Count()) return false;
        return true;
    }
    public void MoveCompositorAggregateItems(CompositorAggregateProperty item, int destination)
    {
        CommitEdits();
        if (!CanMoveCompositorAggregateItems(item, destination)) throw new InvalidOperationException("The selected compositor items cannot be reordered together.");
        var fields = item.Fields.DistinctBy(field => field.Accessor).ToArray();
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var field in fields)
            {
                var node = (IAssetObjectNode)field.Accessor.Node; var index = node.IdToIndex(field.ItemId);
                if (index.Int == destination) continue;
                var value = node.Retrieve(index); var overridden = node.IsItemOverridden(index);
                node.RemoveAndDiscard(value, index, field.ItemId); var next = new NodeIndex(destination);
                node.Restore(value, next, field.ItemId); node.OverrideItem(overridden, next);
            }
        }, "Move " + item.Label);
    }
    public void RenameCompositorAggregateKeys(CompositorAggregateProperty item, string text)
    {
        CommitEdits();
        if (!CanRenameCompositorAggregateKeys(item)) throw new InvalidOperationException("The selected compositor keys cannot be changed together.");
        var plans = item.Fields.DistinctBy(field => field.Accessor).Select(field =>
        {
            var node = (IAssetObjectNode)field.Accessor.Node;
            if (node.Descriptor is not DictionaryDescriptor dictionary) throw new InvalidOperationException("This compositor item is not a dictionary entry.");
            var key = ParseImportDictionaryKey(dictionary.KeyType, text);
            return (Field: field, Node: node, Key: key, Skip: dictionary.ContainsKey(node.Retrieve(), key));
        }).Where(plan => !plan.Skip).ToArray();
        if (plans.Length == 0) return;
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var plan in plans)
            {
                var index = plan.Node.IdToIndex(plan.Field.ItemId); if (Equals(index.Value, plan.Key)) continue;
                var value = plan.Node.Retrieve(index); var overridden = plan.Node.IsItemOverridden(index); var keyOverride = plan.Node.IsKeyOverridden(index);
                plan.Node.RemoveAndDiscard(value, index, plan.Field.ItemId); var next = new NodeIndex(plan.Key);
                plan.Node.Restore(value, next, plan.Field.ItemId); plan.Node.OverrideItem(overridden, next); plan.Node.OverrideKey(keyOverride, next);
            }
        }, "Rename key");
    }
    public bool CanRenameCompositorAggregateKeys(CompositorAggregateProperty item)
        => item.Fields.Count != 0 && item.Fields.All(CanRenameCompositorDictionaryKey);
}
