using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Extensions;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawAggregateCollectionValue(CommonComponentProperty property, IReadOnlyList<CollectionItem> items)
        => DrawAggregateCollectionValue(property, items.Cast<IHierarchyCollectionValue>().ToArray(), create => QueueAggregateMutation(property, field =>
        {
            var item = items[AggregateFieldIndex(property, field)];
            return aggregatePropertyOwners[field].PrepareAggregateCollectionUpdate(item, create(item));
        }, "Edit item: " + property.First.Label), () =>
        {
            DrawAggregateCollectionObjectType(property, items);
            DrawAggregateCollectionChildren(property, items, property.Key + "/item/" + items[0].IdentityKey);
        });
    private void DrawAggregateCollectionValue(CommonComponentProperty property, IReadOnlyList<IHierarchyCollectionValue> items,
        Action<Func<IHierarchyCollectionValue, object?>> apply, Action drawChildren)
    {
        var first = items[0]; var type = first.Type; var value = first.Value;
        if (first.SupportsSimpleEditing && !first.IsContentReference && !first.IsSceneReference
            && items.Select((item, index) => aggregatePropertyOwners[property.Fields[index]].HierarchyItemNativeEntries(item)).Any(entries => entries != null))
            DrawHierarchyItemFactory(property, items);
        if (value is Stride.Core.Yaml.IUnloadable unknown)
        { DrawHierarchyItemFactory(property, items); DrawUnloadableProperty(unknown); return; }
        var mixed = items.Skip(1).Any(item => first.IsContentReference ? !ContentAssetReference.AreEqual(value, item.Value) : !Equals(value, item.Value));
        if (type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory))
        {
            DrawHierarchyItemPaths(property, items, mixed);
            return;
        }
        var key = property.Key + "/item/" + first.IdentityKey;
        void Apply(Func<IHierarchyCollectionValue, object?> create) => apply(create);
        if (first.IsContentReference)
        {
            using var referencePhase = new ContentReferenceCommitPhase(command => pendingCollectionAction = command,
                command => Execute(() => { ValidatePendingPropertyStrings(); command(); }));
            var editable = items.All(item => aggregatePropertyOwners[item.Field].CanEditHierarchyItem(item.Collection, item.Index)
                && (item is HierarchyCollectionItem nested ? aggregatePropertyOwners[item.Field].CanEditNestedHierarchyCollection(nested.Context)
                    : aggregatePropertyOwners[item.Field].CanEditCollection(item.Field)));
            DrawContentReferenceValue(type, value, items.All(item => item.AllowsNull), updated => referencePhase.Submit(() =>
                { Apply(_ => updated); ApplyPendingCollectionAction(); }), mixed, canEdit: editable);
            return;
        }
        if (first.IsSceneReference)
        {
            if (BeginNativeCombo("##Value", mixed ? DifferentPropertyValues : SceneReferenceLabel(value)))
            {
                if (items.All(item => item.AllowsNull) && DrawNativeSelectable(EditorLocalization.Widget("None", "Button"), !mixed && value == null)) Apply(_ => null);
                var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).Distinct().ToArray();
                foreach (var part in owners[0].Asset.Hierarchy.Parts.Values)
                {
                    if (!owners.All(owner => owner.Asset.Hierarchy.Parts.TryGetValue(part.Entity.Id, out var candidate) && ReferenceEquals(candidate.Entity, part.Entity))) continue;
                    IEnumerable<object> candidates = SceneObjectReference.AcceptsEntity(type, part.Entity) ? [part.Entity] : part.Entity.Components.Where(type.IsInstanceOfType);
                    foreach (var candidate in candidates)
                        if (DrawNativeSelectable(SceneReferenceLabel(candidate), !mixed && ReferenceEquals(candidate, value))) Apply(_ => candidate);
                }
                Gui.EndCombo();
            }
            return;
        }
        if (!first.SupportsSimpleEditing)
        {
            drawChildren();
            return;
        }
        if (UsesAggregateItemDraft(first)) { DrawAggregateItemDrafts(property, items); return; }
        var nullable = items.All(item => Nullable.GetUnderlyingType(item.DeclaredType) != null);
        using var nullableRow = NativeNullableRow.Begin("##Nullable", nullable);
        if (nullable)
        {
            var present = items.Any(item => item.Value != null);
            using (ImGuiScopes.Disabled(!CanEditAggregateCollection(property)
                || items.Select((item, index) => !aggregatePropertyOwners[property.Fields[index]].CanEditHierarchyItem(item.Collection, item.Index)).Any(readOnly => readOnly)))
                if (DrawNativeNullablePresence(present, out var create, items.All(item => item.AllowsNull))) Apply(item => create ? item.Value ?? (type == typeof(string) ? "" : CreateNullableValue(type)) : null);
            nullableRow.ValueColumn();
            if (items.All(item => item.Value == null)) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
        }
        if (type == typeof(string)) { DrawAggregateCollectionString(property, items); return; }
        if (IsPackageColorType(type)) { DrawRetainedItemColor(property, items); return; }
        if (items.Any(item => item.Value == null) && !AssetSettingNumericMetadata.Supports(type)) return;
        if (DrawNativeScalarTemplate(first.Collection, type, value, mixed, items.Select((item, ownerIndex) => aggregatePropertyOwners[property.Fields[ownerIndex]].CanEditHierarchyItem(item.Collection, item.Index)).All(canEdit => canEdit), updated => Apply(_ => updated))) { }
        else if (type == typeof(bool))
        {
            var enabled = value is true; Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
            if (DrawNativeCheckbox("##Value", ref enabled)) Apply(_ => enabled); Gui.PopItemFlag();
        }
        else if (type.IsEnum)
        {
            if (icons?.HasEnumImages(type) == true)
            {
                if (NativeEnumImageControl.Draw(icons, first.Collection, type, value, mixed,
                    first.EnumOptions.Select(option => (Enum)option.Value), UiScale(1), out var updated)) Apply(_ => updated);
                if (type.IsDefined(typeof(FlagsAttribute), true) && Gui.BeginPopupContextItem("Flag actions"))
                {
                    if (DrawFlagBulkActions() is { } action)
                        Apply(item => ApplyFlagBulkAction((Enum)(item.Value ?? Enum.ToObject(type, 0)), action));
                    Gui.EndPopup();
                }
            }
            else if (BeginNativeCombo("##Value", mixed ? DifferentPropertyValues : first.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString()))
            {
                foreach (var option in first.EnumOptions)
                {
                    if (!type.IsDefined(typeof(FlagsAttribute), true)) { if (DrawNativeSelectable(option.Label, !mixed && Equals(option.Value, value))) Apply(_ => option.Value); continue; }
                    var mask = EnumBits(option.Value); var bits = EnumBits(value!); var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                    var different = items.Skip(1).Any(item => { var actual = EnumBits(item.Value!); return (mask == 0 ? actual == 0 : (actual & mask) == mask) != selected; });
                    Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, different);
                    if (DrawNativeCheckbox(option.Label, ref selected)) Apply(item => { var actual = EnumBits(item.Value!); return EnumFromBits(type, mask == 0 ? 0 : selected ? actual | mask : actual & ~mask); });
                    Gui.PopItemFlag();
                }
                if (type.IsDefined(typeof(FlagsAttribute), true) && DrawFlagBulkActions() is { } action)
                    Apply(item => ApplyFlagBulkAction((Enum)(item.Value ?? Enum.ToObject(type, 0)), action));
                Gui.EndCombo();
            }
        }
        else if (type == typeof(Color) || type == typeof(Color3) || type == typeof(Color4))
            DrawCommonColorValue(key, type, items.Select(item => item.Value).ToArray(), create => Apply(item => create(item.Value)),
                axisRange: axis => NativeNumericInput.Merge(type == typeof(Color) ? typeof(byte) : typeof(float), items.Select(item => item.AxisNumericRanges.ElementAtOrDefault(axis))),
                axisCanEdit: axis => items.Select((item, ownerIndex) => aggregatePropertyOwners[property.Fields[ownerIndex]].IsHierarchyItemAxisReadOnly(item, axis)).All(readOnly => !readOnly));
        else if (type == typeof(Quaternion))
        {
            var rotationView = Gui.GetID("##Rotation");
            var rotations = items.Select(item => RotationEditor(item, rotationView)).ToArray();
            for (var axis = 0; axis < 3; axis++)
            {
                var index = axis; Gui.PushID(axis); Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine();
                var coordinate = rotations[0].Read((Quaternion)(value ?? Quaternion.Identity))[axis];
                AggregateCollectionText(key + "/" + axis, coordinate, items.Select((item, position) => rotations[position].Read((Quaternion)(item.Value ?? Quaternion.Identity))[axis]).Skip(1).Any(other => other != coordinate),
                    text => Apply(item => RotationEditor(item, rotationView).ReplaceAxis((Quaternion)(item.Value ?? Quaternion.Identity), index,
                        float.Parse(text, CultureInfo.InvariantCulture))), numericType: typeof(float)); Gui.PopID();
            }
        }
        else if (CommonValueDimensions(type) is var dimensions && dimensions > 0)
        {
            var matrix = UsesExtendedMathTable(type) && Gui.BeginTable("##Matrix", 4, ImGuiTableFlags.SizingStretchSame);
            for (var axis = 0; axis < dimensions && (!UsesExtendedMathTable(type) || matrix); axis++)
            {
                using var axisDisabled = ImGuiScopes.Disabled(items.Select((item, ownerIndex) => aggregatePropertyOwners[property.Fields[ownerIndex]].IsHierarchyItemAxisReadOnly(item, axis)).Any(readOnly => readOnly));
                var index = axis; Gui.PushID(axis); DrawExtendedMathCoordinateLabel(type, axis, matrix);
                var coordinate = CommonCoordinate(value, axis);
                AggregateCollectionText(key + "/" + axis, coordinate, items.Skip(1).Any(item => !Equals(CommonCoordinate(item.Value, axis), coordinate)), text => Apply(item => WithCommonCoordinate(item.Value, type, index,
                    type == typeof(UInt4) ? uint.Parse(text, CultureInfo.InvariantCulture) : type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(Rectangle) ? (object)int.Parse(text, CultureInfo.InvariantCulture) : float.Parse(text, CultureInfo.InvariantCulture))),
                    numericType: PropertyCoordinateType(type, axis), numericRange: () => NativeNumericInput.Merge(PropertyCoordinateType(type, index), items.Select(item => item.AxisNumericRanges.ElementAtOrDefault(index))));
                Gui.PopID();
            }
            if (matrix) Gui.EndTable();
        }
        else AggregateCollectionText(key, value, mixed, text => Apply(_ => type == typeof(string) ? text : type == typeof(decimal)
            ? decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture) : Convert.ChangeType(text, type, CultureInfo.InvariantCulture)),
            enabled: CanEditAggregateCollection(property) && items.Select((item, index) => aggregatePropertyOwners[property.Fields[index]].CanEditHierarchyItem(item.Collection, item.Index)).All(editable => editable),
            numericType: type, numericRange: () => AssetSettingNumericMetadata.Supports(type) ? NativeNumericInput.Merge(type,
                items.Select((item, index) => aggregatePropertyOwners[property.Fields[index]].HierarchyItemNumericRange(item))) : null,
            rangedRanges: Nullable.GetUnderlyingType(first.DeclaredType) == null ? () => items.Select((item, ownerIndex) => aggregatePropertyOwners[property.Fields[ownerIndex]].HierarchyItemNumericRange(item)).ToArray() : null,
            rangedOwner: first.Collection);
    }
    private void DrawAggregateCollectionObjectType(CommonComponentProperty property, IReadOnlyList<CollectionItem> items)
        => DrawHierarchyItemFactory(property, items.Cast<IHierarchyCollectionValue>().ToArray());
    private void DrawAggregateCollectionChildren(CommonComponentProperty collection, IReadOnlyList<CollectionItem> items, string key)
    {
        static IEnumerable<ComponentField> Leaves(IEnumerable<ComponentField> fields)
        {
            foreach (var field in fields)
            {
                if (field.IsIndexedCollection || field.IsDictionary || field.SupportsEditing) yield return field;
                else { if (field.SupportsObjectLifecycle) yield return field; foreach (var child in Leaves(field.Children)) yield return child; }
            }
        }
        var maps = items.Select((item, index) =>
        {
            var owner = aggregatePropertyOwners[collection.Fields[index]]; var prefix = item.Field.Name + "/" + item.Id + "/";
            return Leaves(owner.GetCollectionItemFields(item)).ToDictionary(field => field.Name[prefix.Length..]);
        }).ToArray();
        foreach (var entry in maps[0])
        {
            var fields = new List<ComponentField> { entry.Value };
            foreach (var map in maps.Skip(1))
            {
                if (!map.TryGetValue(entry.Key, out var field) || field.DeclaredType != entry.Value.DeclaredType
                    || !Equals(field.Node.MemberDescriptor.MemberInfo, entry.Value.Node.MemberDescriptor.MemberInfo)) break;
                fields.Add(field);
            }
            if (fields.Count != maps.Length) continue;
            for (var index = 0; index < fields.Count; index++) aggregatePropertyOwners[fields[index]] = aggregatePropertyOwners[collection.Fields[index]];
            DrawAggregateProperty(new(key + "/" + entry.Key, collection.ComponentLabel, fields));
        }
    }
}
