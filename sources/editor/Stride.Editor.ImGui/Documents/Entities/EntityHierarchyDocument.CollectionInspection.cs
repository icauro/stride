using System.Collections;
using Stride.Core.Assets.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private int lastHierarchyCapturedChanges, lastHierarchyStructuralChanges, lastHierarchyAffectedComponents;
    internal object InspectCollectionOrdering(ComponentField field)
    {
        static string? Identity(object? value) => value switch
        {
            Entity entity => "Entity:" + entity.Id,
            EntityComponent component => "Component:" + component.Id,
            null => null,
            _ => value.GetType().FullName,
        };
        static string?[] Order(object? value) => value is IEnumerable enumerable ? enumerable.Cast<object?>().Select(Identity).ToArray() : [];
        var source = field.ReadRuntime(field.Component);
        var member = field.Node.Retrieve();
        var collection = field.Node.Target as IAssetObjectNode;
        var target = collection?.Retrieve();
        var cached = collectionItems.TryGetValue(field, out var items) ? items : null;
        return new
        {
            StructureVersion,
            componentVersion = ComponentReplacementVersion(field.Component.Id),
            lastHierarchyCapturedChanges, lastHierarchyStructuralChanges, lastHierarchyAffectedComponents,
            sourceOrder = Order(source), memberOrder = Order(member), targetOrder = Order(target),
            sourceIsMember = ReferenceEquals(source, member), memberIsTarget = ReferenceEquals(member, target),
            targetGraphIsCurrent = collection != null && ReferenceEquals(collection.PropertyGraph, hierarchyGraph),
            collectionNode = collection?.Guid,
            native = (collection?.Indices ?? []).Select(index => new
            {
                index = index.ToString(), itemId = collection!.IndexToId(index).ToString(), value = Identity(collection.Retrieve(index)),
            }).ToArray(),
            cached = cached?.Select(item => new
            {
                itemId = item.Id.ToString(), index = item.Index.ToString(), value = Identity(item.Value),
                currentTarget = ReferenceEquals(item.Collection, collection),
            }).ToArray(),
        };
    }
}
