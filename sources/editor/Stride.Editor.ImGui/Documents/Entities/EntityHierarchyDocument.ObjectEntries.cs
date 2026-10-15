using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Quantum.Visitors;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal IReadOnlyList<AbstractNodeEntry> ObjectEntries(ComponentField field)
        => NativeAbstractEntries.Available(field.Presentation?.AbstractEntries(field), field.ObjectTypes, field.AllowsNullObject);

    internal Action PrepareAggregateObjectEntry(ComponentField field, AbstractNodeEntry entry)
    {
        if (!CanEditAggregateObject(field) || !NativeAbstractEntries.Contains(ObjectEntries(field), entry))
            throw new InvalidOperationException("The selected native object entry is no longer available.");
        var before = field.Node.Retrieve();
        if (entry.IsMatchingValue(before!)) return () => { };
        var value = entry.GenerateValue(before!);
        if (value == null ? !field.AllowsNullObject : !field.DeclaredType.IsInstanceOfType(value))
            throw new ArgumentException("The object entry returned an incompatible value.");
        object? after = null;
        if (value != null)
        {
            var node = HierarchyGraph.Container.NodeContainer.GetOrCreateNode(value);
            after = AssetCloner.Clone(value, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects,
                ExternalReferenceCollector.GetExternalReferences(HierarchyGraph.Definition, node), out _);
            AssetCollectionItemIdHelper.GenerateMissingItemIds(after!);
        }
        return () =>
        {
            if (!CanEditAggregateObject(field) || !NativeAbstractEntries.Contains(ObjectEntries(field), entry)
                || !SameAggregateOwnedValue(field.Node.Retrieve(), before))
                throw new InvalidOperationException("The selected object or native entry changed.");
            var removed = before == null || field.Node.Target == null ? new HashSet<Guid>()
                : IdentifiableObjectCollector.Collect(HierarchyGraph.Definition, field.Node.Target).Keys.ToHashSet();
            if (before is IIdentifiable identifiable) removed.Add(identifiable.Id);
            field.Node.Update(after);
            if (removed.Count != 0) HierarchyGraph.ClearReferencesToObjects(removed);
        };
    }
    internal void ChangeObjectEntry(ComponentField field, AbstractNodeEntry entry)
    {
        if (!CanChangeObjectProperty(field)) throw new InvalidOperationException("This object property cannot be changed.");
        EndEdits();
        var change = PrepareAggregateObjectEntry(field, entry);
        ApplyInheritedGraphMutation(change, "Edit " + field.Label);
    }
}
