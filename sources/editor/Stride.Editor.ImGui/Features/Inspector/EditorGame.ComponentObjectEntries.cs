using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void SelectComponentObjectEntry(EntityHierarchyDocument owner, ComponentField field, AbstractNodeEntry entry)
        => Execute(() =>
        {
            if (field.Component.Entity is not { } entity || !owner.IsSelected(entity.Id) || !owner.CanChangeObjectProperty(field)
                || !NativeAbstractEntries.Contains(owner.ObjectEntries(field), entry))
                throw new InvalidOperationException("The native object entry is no longer available.");
            RunHierarchyFactoryCommand([owner], AcquireHierarchyFactoryTargets(1, _ => owner.AcquireExtensionCommandTarget(field.Node)),
                "CreateNewInstance", entry, () => owner.ChangeObjectEntry(field, entry));
        });
    private void SelectAggregateObjectEntry(CommonComponentProperty property, AbstractNodeEntry entry)
        => Execute(() =>
        {
            var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).ToArray();
            if (owners.Length == 0 || property.Fields.Where((field, index) => !owners[index].CanEditAggregateObject(field)
                    || !NativeAbstractEntries.Contains(owners[index].ObjectEntries(field), entry)).Any())
                throw new InvalidOperationException("The selected native object entry is no longer available.");
            RunHierarchyFactoryCommand(owners, AcquireHierarchyFactoryTargets(property.Fields.Count,
                index => owners[index].AcquireExtensionCommandTarget(property.Fields[index].Node)), "CreateNewInstance", entry,
                () => ApplyAggregateMutation(property, field => aggregatePropertyOwners[field].PrepareAggregateObjectEntry(field, entry), "Edit " + property.First.Label));
        });
}