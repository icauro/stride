using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedSingleItemColor(CollectionItem item)
    {
        var owner = document!;
        bool Editable() => ReferenceEquals(document, owner) && !IsAuthoringPipelineUpdating && item.Collection.HasId(item.Id)
            && ReferenceEquals(item.Field.Node.Target, item.Collection) && item.Field.Component.Entity is { } entity && owner.IsSelected(entity.Id)
            && owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index);
        DrawRetainedColor(item.Collection, item.Type, () => [item.Value, item.Index, item.Id], Editable,
            create => { owner.UpdateCollectionItem(item, create(item.Value)); owner.EndCollectionEdit(); },
            () => AcquirePropertyStringOwners([owner]), axis => !owner.IsHierarchyItemAxisReadOnly(item, axis));
    }
    private void DrawRetainedCombinedColor(CommonComponentProperty property, bool aggregate = false, bool inherited = false)
    {
        var fields = property.Fields.ToArray(); var owner = document!;
        var fieldOwners = fields.Select(field => aggregate ? aggregatePropertyOwners[field] : owner).ToArray();
        bool Editable() => !IsAuthoringPipelineUpdating && fields.Select((field, index) => field.Component.Entity is { } entity && fieldOwners[index].IsSelected(entity.Id)).All(current => current)
            && (aggregate ? fields.Select((field, index) => aggregatePropertyOwners.TryGetValue(field, out var current) && ReferenceEquals(current, fieldOwners[index])).All(current => current) && CanEditAggregateProperty(property)
                : ReferenceEquals(document, owner) && (inherited ? CanEditInheritedCommon(property) : owner.CanEditCommonProperty(property)));
        void Apply(System.Func<object?, object?> create)
        {
            if (aggregate) ApplyAggregateProperty(property, field => create(field.Node.Retrieve()));
            else if (inherited) owner.EditInheritedScalars(fields, fields.Select(field => create(field.Node.Retrieve())).ToArray());
            else { owner.UpdateCommonPropertyValues(property, field => create(field.Node.Retrieve())); owner.EndCommonPropertyEdit(); }
        }
        void Reset() { if (aggregate) DrawAggregateReset(property); else if (inherited) DrawInheritedCommonReset(property); else DrawCommonPropertyActions(property); }
        using var nullable = NativeNullableRow.Begin("##Nullable", fields[0].IsNullable);
        if (fields[0].IsNullable)
        {
            var present = fields.Any(field => field.Node.Retrieve() != null);
            using (ImGuiScopes.Disabled(!Editable()))
                if (DrawNativeNullablePresence(present, out var create, fields.All(field => field.AllowsNullObject))) Execute(() => Apply(value => create ? value ?? CreateNullableValue(fields[0].Type) : null));
            nullable.ValueColumn(); if (!present) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); Reset(); return; }
        }
        DrawRetainedColor(fields[0].Node, fields[0].Type, () => fields.Select(field => field.Node.Retrieve()).ToArray(), Editable,
            Apply, () => AcquirePropertyStringOwners(fieldOwners), axis => fields.All(field => !field.IsAxisReadOnly(axis)), Reset, valueCount: fields.Length);
    }

    private void DrawRetainedItemColor(CommonComponentProperty property, IReadOnlyList<IHierarchyCollectionValue> source)
    {
        var items = source.ToArray(); var fields = property.Fields.ToArray(); var owners = fields.Select(field => aggregatePropertyOwners[field]).ToArray();
        bool Editable() => !IsAuthoringPipelineUpdating && items.Select((item, index) => aggregatePropertyOwners.TryGetValue(fields[index], out var current) && ReferenceEquals(current, owners[index])
            && item.Field.Component.Entity is { } entity && owners[index].IsSelected(entity.Id)
            && (item is CollectionItem regular ? regular.Collection.HasId(regular.Id) && owners[index].CanEditCollection(item.Field) : item is HierarchyCollectionItem nested && owners[index].CanEditNestedHierarchyCollection(nested.Context))
            && owners[index].CanEditHierarchyItem(item.Collection, item.Index)).All(editable => editable);
        object?[] Read()
        {
            var baseline = items.Select(item => item.Value).ToList();
            foreach (var item in items)
            {
                baseline.Add(item.Collection); baseline.Add(item.Index); baseline.Add(item.IdentityKey);
                if (item is HierarchyCollectionItem nested)
                    for (var parent = nested.Context.Parent; parent != null; parent = parent.Context.Parent)
                    { baseline.Add(parent.Collection); baseline.Add(parent.Index); baseline.Add(parent.IdentityKey); }
            }
            return baseline.ToArray();
        }
        DrawRetainedColor(items[0].Collection, items[0].Type, Read, Editable, create => ApplyAggregateMutation(property, field =>
        {
            var index = Array.IndexOf(fields, field); var item = items[index]; var value = create(item.Value);
            return item switch
            {
                CollectionItem regular => owners[index].PrepareAggregateCollectionUpdate(regular, value),
                HierarchyCollectionItem nested => owners[index].PrepareNestedHierarchyUpdate(nested, value),
                _ => throw new InvalidOperationException("The color item changed.")
            };
        }, "Edit item: " + property.First.Label), () => AcquirePropertyStringOwners(owners),
            axis => items.Select((item, index) => !owners[index].IsHierarchyItemAxisReadOnly(item, axis)).All(editable => editable), valueCount: items.Length);
    }
}
