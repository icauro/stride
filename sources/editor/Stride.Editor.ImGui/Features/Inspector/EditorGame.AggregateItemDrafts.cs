using System.Globalization;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool UsesAggregateItemDraft(IHierarchyCollectionValue item) => item.Type == typeof(string)
        || AssetSettingNumericMetadata.Supports(item.Type) || IsPathType(item.Type) || IsTemporalType(item.Type) || item.Type == typeof(char) || item.Type == typeof(Guid) || item.Type == typeof(AngleSingle) || item.Type == typeof(Quaternion)
        || item.Type != typeof(Color) && item.Type != typeof(Color3) && item.Type != typeof(Color4) && CommonValueDimensions(item.Type) > 0;

    private void DrawAggregateItemDrafts(CommonComponentProperty property, IReadOnlyList<IHierarchyCollectionValue> source)
    {
        var items = source.ToArray(); var fields = property.Fields.ToArray(); var first = items[0]; var type = first.Type;
        var fieldOwners = fields.Select(field => aggregatePropertyOwners[field]).ToArray(); var owners = fieldOwners.Distinct().ToArray();
        object?[] Read()
        {
            var baseline = items.Select(item => item.Value).ToList();
            foreach (var item in items)
            {
                baseline.Add(item.Collection); baseline.Add(item.Index); baseline.Add(item.IdentityKey);
                if (item is not HierarchyCollectionItem nested) continue;
                for (var parent = nested.Context.Parent; parent != null; parent = parent.Context.Parent)
                {
                    baseline.Add(parent.Collection); baseline.Add(parent.Index); baseline.Add(parent.IdentityKey);
                }
            }
            return baseline.ToArray();
        }
        bool Editable() => !IsAuthoringPipelineUpdating && CanEditAggregateCollection(property)
            && items.Select((item, index) => aggregatePropertyOwners.TryGetValue(fields[index], out var owner)
                && ReferenceEquals(owner, fieldOwners[index]) && fields[index].Component.Entity is { } entity && owner.IsSelected(entity.Id)
                && (item is CollectionItem regular ? regular.Collection.HasId(regular.Id) && ReferenceEquals(regular.Field.Node.Target, regular.Collection)
                    : item is HierarchyCollectionItem nested && owner.CanEditNestedHierarchyCollection(nested.Context)
                        && (nested.Id == null || nested.Collection.HasId(nested.Id.Value)))
                && owner.CanEditHierarchyItem(item.Collection, item.Index)).All(current => current);
        void Apply(Func<IHierarchyCollectionValue, object?> create) => ApplyAggregateMutation(property, field =>
        {
            var index = Array.IndexOf(fields, field); var item = items[index]; var owner = fieldOwners[index];
            var value = create(item);
            return item switch
            {
                CollectionItem regular => owner.PrepareAggregateCollectionUpdate(regular, value),
                HierarchyCollectionItem nested => owner.PrepareNestedHierarchyUpdate(nested, value),
                _ => throw new InvalidOperationException("The property item is no longer current.")
            };
        }, "Edit item: " + property.First.Label);
        var nullable = items.All(item => Nullable.GetUnderlyingType(item.DeclaredType) != null);
        using var nullableRow = NativeNullableRow.Begin("##Nullable", nullable);
        if (nullable)
        {
            var present = items.Any(item => item.Value != null);
            using (ImGuiScopes.Disabled(!Editable()))
                if (DrawNativeNullablePresence(present, out var create, items.All(item => item.AllowsNull)))
                    Execute(() => Apply(item => create ? item.Value ?? CreateNullableValue(type) : null));
            nullableRow.ValueColumn(); if (!present) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
        }
        if (IsPathType(type))
            DrawRetainedPath(owners[0], first.Collection, type, Editable, Read,
                path => Apply(_ => path), items.Length, () => AcquirePropertyStringOwners(owners));
        else if (IsTemporalType(type))
            DrawRetainedTemporal(first.Collection, type, Editable, Read, transform => Apply(item => transform(item.Value)),
                items.Length, () => AcquirePropertyStringOwners(owners));
        else if (type == typeof(char))
            DrawRetainedCharacter(first.Collection, Read, Editable, character => Apply(_ => character), () => AcquirePropertyStringOwners(owners), items.Length);
        else if (type == typeof(Guid))
            DrawRetainedGuid(first.Collection, Read, Editable, guid => Apply(_ => guid), () => AcquirePropertyStringOwners(owners), items.Length);
        else if (type == typeof(AngleSingle))
            DrawRetainedAngle(first.Collection, Read, Editable, angle => Apply(_ => angle), () => AcquirePropertyStringOwners(owners), items.Length);
        else if (type == typeof(string) || AssetSettingNumericMetadata.Supports(type)) Draw(-1);
        else
        {
            var rotationView = type == typeof(Quaternion) ? Gui.GetID("##Rotation") : (uint?)null;
            var count = rotationView != null ? 3 : CommonValueDimensions(type);
            var tableRequired = UsesExtendedMathTable(type);
            var table = tableRequired && Gui.BeginTable("##Matrix", 4, Hexa.NET.ImGui.ImGuiTableFlags.SizingStretchSame);
            for (var axis = 0; axis < count && (!tableRequired || table); axis++)
            {
                Gui.PushID(axis);
                if (rotationView != null) { Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine(); }
                else DrawExtendedMathCoordinateLabel(type, axis, table);
                Draw(axis, rotationView); Gui.PopID();
            }
            if (table) Gui.EndTable();
        }
        void Draw(int axis, uint? rotationView = null)
        {
            var scalarType = PropertyCoordinateType(type, axis);
            var rotations = rotationView is { } view ? items.Select(item => RotationEditor(item, view)).ToArray() : null;
            object? Coordinate(object? raw, int index) => axis < 0 ? raw : rotations != null && raw is Quaternion quaternion
                ? rotations[index].Read(quaternion)[axis] : CommonCoordinate(raw, axis);
            var coordinate = Coordinate(first.Value, 0);
            var mixed = items.Select((item, index) => !Equals(Coordinate(item.Value, index), coordinate)).Any(different => different);
            var ranges = items.Select((item, index) => axis < 0 ? fieldOwners[index].HierarchyItemNumericRange(item)
                : rotations != null ? null : item.AxisNumericRanges.ElementAtOrDefault(axis)).ToArray();
            var range = scalarType == typeof(string) ? null : NativeNumericInput.Merge(scalarType, ranges);
            bool AxisEditable() => Editable() && (axis < 0 || rotations != null
                || items.Select((item, index) => !fieldOwners[index].IsHierarchyItemAxisReadOnly(item, axis)).All(editable => editable));
            var ranged = axis < 0 && scalarType != typeof(string) && items.Select((item, index) => NativeRangedNumericControl.Matches(item.DeclaredType, ranges[index])).All(matches => matches);
            object? display = coordinate; var sliderActive = false; var sliderCommit = false;
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!AxisEditable());
                sliderCommit = NativeRangedNumericControl.Draw(first.Collection, scalarType, coordinate, mixed, ranges, UiScale(1), out display, out _);
                sliderActive = Gui.IsItemActive();
            }
            Gui.SetNextItemWidth(-1); var feedback = Gui.GetID("##Value");
            try
            {
                PropertyGrid.Strings.Draw(first.Collection, mixed, AxisEditable,
                    Read, text =>
                    {
                        var snapshots = rotations?.Select(rotation => rotation.Capture()).ToArray();
                        try
                        {
                            var number = PropertyConversionFeedback.Convert(feedback, () => scalarType == typeof(string) ? (object)text
                                : scalarType == typeof(decimal) && !ranged ? decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)
                                : NativeNumericText.Parse(text, scalarType, range));
                            Apply(item =>
                            {
                                var index = Array.IndexOf(items, item); if (axis < 0) return number;
                                var value = item.Value ?? (nullable ? CreateNullableValue(type) : null);
                                var component = scalarType == typeof(float) ? (object)Convert.ToSingle(PropertyValueValidation.Normalize(number, ranges[index]), CultureInfo.InvariantCulture) : number;
                                if (component is float scalar && !float.IsFinite(scalar)) throw new ArgumentException("The coordinate must be finite.");
                                return rotations != null ? rotations[index].ReplaceAxis(value is Quaternion quaternion ? quaternion : Quaternion.Identity, axis, (float)component)
                                    : WithCommonCoordinate(value, type, axis, component);
                            });
                        }
                        catch { if (snapshots != null) for (var index = 0; index < snapshots.Length; index++) rotations![index].Restore(snapshots[index]); throw; }
                    }, () => AcquirePropertyStringOwners(owners), text =>
                    {
                        if (scalarType == typeof(string) || scalarType == typeof(decimal) && !ranged) return null;
                        return NativeNumericInput.StepText(scalarType, Coordinate(first.Value, 0), mixed, range, ref text) ? text : null;
                    }, raw => Convert.ToString(Coordinate(raw, 0), CultureInfo.InvariantCulture) ?? "",
                    externalText: ranged && display != null && (mixed || !Equals(display, coordinate)) ? Convert.ToString(display, CultureInfo.InvariantCulture) : null,
                    externalActive: sliderActive, externalCommit: sliderCommit,
                    onCancelled: error => ClearCancelledPropertyDraft(error, feedback));
            }
            catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); SetPropertyDraftDiagnostic(error); }
            PropertyConversionFeedback.Draw(feedback);
        }
    }
}
