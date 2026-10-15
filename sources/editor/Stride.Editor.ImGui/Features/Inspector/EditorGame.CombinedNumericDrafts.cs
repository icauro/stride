using System.Globalization;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool UsesCombinedNumericDraft(CommonComponentProperty property)
        => !property.First.IsColor && !property.First.IsNavigationGroup && (AssetSettingNumericMetadata.Supports(property.First.Type)
            || IsPathType(property.First.Type) || IsTemporalType(property.First.Type) || property.First.Type == typeof(char) || property.First.Type == typeof(Guid) || property.First.Type == typeof(AngleSingle) || property.First.Type == typeof(Quaternion) || CommonValueDimensions(property.First.Type) > 0);

    private void DrawCombinedNumericDrafts(CommonComponentProperty property, bool aggregate = false, bool inherited = false)
    {
        var fields = property.Fields.ToArray(); var first = fields[0]; var type = first.Type;
        var commonOwner = document!;
        var fieldOwners = fields.Select(field => aggregate ? aggregatePropertyOwners[field] : commonOwner).ToArray();
        var owners = fieldOwners.Distinct().ToArray();
        bool Editable() => !IsAuthoringPipelineUpdating
            && fields.Select((field, index) => field.Component.Entity is { } entity && fieldOwners[index].IsSelected(entity.Id)).All(current => current)
            && (aggregate ? fields.Select((field, index) => aggregatePropertyOwners.TryGetValue(field, out var current)
                    && ReferenceEquals(current, fieldOwners[index])).All(current => current) && CanEditAggregateProperty(property)
                : ReferenceEquals(document, commonOwner) && (inherited ? CanEditInheritedCommon(property) : commonOwner.CanEditCommonProperty(property)));
        void Apply(Func<ComponentField, object?> create)
        {
            if (aggregate) ApplyAggregateProperty(property, create);
            else if (inherited) commonOwner.EditInheritedScalars(fields, fields.Select(create).ToArray());
            else { commonOwner.UpdateCommonPropertyValues(property, create); commonOwner.EndCommonPropertyEdit(); }
        }
        void Reset() { if (aggregate) DrawAggregateReset(property); else if (inherited) DrawInheritedCommonReset(property); else DrawCommonPropertyActions(property); }
        using var nullableRow = NativeNullableRow.Begin("##Nullable", first.IsNullable);
        if (first.IsNullable)
        {
            var present = fields.Any(field => field.Node.Retrieve() != null);
            using (ImGuiScopes.Disabled(!Editable()))
                if (DrawNativeNullablePresence(present, out var create, fields.All(field => field.AllowsNullObject)))
                    Execute(() => Apply(field => create ? field.Node.Retrieve() ?? CreateNullableValue(type) : null));
            nullableRow.ValueColumn();
            if (!present) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); Reset(); return; }
        }
        if (IsPathType(type))
        {
            DrawRetainedPath(owners[0], first.Node, type, Editable, () => fields.Select(field => field.Node.Retrieve()).ToArray(),
                path => Apply(_ => path), fields.Length, () => AcquirePropertyStringOwners(owners));
            Reset();
        }
        else if (IsTemporalType(type))
        {
            DrawRetainedTemporal(first.Node, type, Editable, () => fields.Select(field => field.Node.Retrieve()).ToArray(),
                transform => Apply(field => transform(field.Node.Retrieve())), fields.Length, () => AcquirePropertyStringOwners(owners));
            Reset();
        }
        else if (type == typeof(char))
        {
            DrawRetainedCharacter(first.Node, () => fields.Select(field => field.Node.Retrieve()).ToArray(), Editable,
                character => Apply(_ => character), () => AcquirePropertyStringOwners(owners), fields.Length);
            Reset();
        }
        else if (type == typeof(Guid))
        {
            DrawRetainedGuid(first.Node, () => fields.Select(field => field.Node.Retrieve()).ToArray(), Editable,
                guid => Apply(_ => guid), () => AcquirePropertyStringOwners(owners), fields.Length);
            Reset();
        }
        else if (type == typeof(AngleSingle))
        {
            DrawRetainedAngle(first.Node, () => fields.Select(field => field.Node.Retrieve()).ToArray(), Editable,
                angle => Apply(_ => angle), () => AcquirePropertyStringOwners(owners), fields.Length);
            Reset();
        }
        else if (AssetSettingNumericMetadata.Supports(type)) Draw(-1);
        else
        {
            var rotationView = type == typeof(Quaternion) ? Gui.GetID("##Rotation") : (uint?)null;
            var dimensions = rotationView != null ? 3 : CommonValueDimensions(type);
            var tableRequired = UsesExtendedMathTable(type);
            var table = tableRequired && Gui.BeginTable("##Matrix", 4, Hexa.NET.ImGui.ImGuiTableFlags.SizingStretchSame);
            for (var axis = 0; axis < dimensions && (!tableRequired || table); axis++)
            {
                Gui.PushID(axis);
                if (rotationView != null) { Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine(); }
                else DrawExtendedMathCoordinateLabel(type, axis, table);
                Gui.SetNextItemWidth(-1); Draw(axis, rotationView); Gui.PopID();
            }
            if (table) Gui.EndTable();
        }
        void Draw(int axis, uint? rotationView = null)
        {
            var scalarType = PropertyCoordinateType(type, axis);
            var rotations = rotationView is { } view ? fields.Select(field => RotationEditor(field.Node, view)).ToArray() : null;
            object? Coordinate(object? raw, int index) => axis < 0 ? raw : rotations != null && raw is Quaternion quaternion
                ? rotations[index].Read(quaternion)[axis] : CommonCoordinate(raw, axis);
            var values = fields.Select(field => field.Node.Retrieve()).ToArray();
            var coordinate = Coordinate(values[0], 0);
            var mixed = values.Select((value, index) => !Equals(Coordinate(value, index), coordinate)).Any(different => different);
            var ranges = fields.Select(field => rotations != null ? null : FieldCoordinateRange(field, axis)).ToArray();
            var range = NativeNumericInput.Merge(scalarType, ranges);
            bool AxisEditable() => Editable() && (axis < 0 || rotations != null || fields.All(field => !field.IsAxisReadOnly(axis)));
            var ranged = axis < 0 && fields.All(field => NativeRangedNumericControl.Matches(field.DeclaredType, field.EffectiveNumericRange));
            object? display = coordinate; var sliderActive = false; var sliderCommit = false;
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!AxisEditable());
                sliderCommit = NativeRangedNumericControl.Draw(first.Node, scalarType, coordinate, mixed, ranges, UiScale(1), out display, out _);
                sliderActive = Gui.IsItemActive();
            }
            var feedback = Gui.GetID("##Value"); Gui.SetNextItemWidth(-1);
            string Format(object? raw) => Coordinate(raw, 0) is float number && axis >= 0
                ? number.ToString("F" + (range?.DecimalPlaces ?? 3), CultureInfo.InvariantCulture)
                : Convert.ToString(Coordinate(raw, 0), CultureInfo.InvariantCulture) ?? "";
            try
            {
                PropertyGrid.Strings.Draw(first.Node, mixed, AxisEditable, () => fields.Select(field => field.Node.Retrieve()).ToArray(), text =>
                {
                    var snapshots = rotations?.Select(rotation => rotation.Capture()).ToArray();
                    try
                    {
                        var number = PropertyConversionFeedback.Convert(feedback, () => scalarType == typeof(decimal) && !ranged
                            ? (object)decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)
                            : NativeNumericText.Parse(text, scalarType, range));
                        if (number is float scalar && !float.IsFinite(scalar)) throw new ArgumentException("The coordinate must be finite.");
                        Apply(field =>
                        {
                            var index = Array.IndexOf(fields, field); var current = field.Node.Retrieve();
                            if (axis < 0) return number;
                            if (current == null && field.IsNullable) current = CreateNullableValue(type);
                            var axisValue = scalarType == typeof(float)
                                ? (object)Convert.ToSingle(PropertyValueValidation.Normalize(number, ranges[index]), CultureInfo.InvariantCulture) : number;
                            if (axisValue is float component && !float.IsFinite(component)) throw new ArgumentException("The coordinate must be finite.");
                            return rotations != null
                                ? rotations[index].ReplaceAxis(current is Quaternion quaternion ? quaternion : Quaternion.Identity, axis, (float)axisValue)
                                : WithCommonCoordinate(current, type, axis, axisValue);
                        });
                    }
                    catch
                    {
                        if (snapshots != null) for (var index = 0; index < snapshots.Length; index++) rotations![index].Restore(snapshots[index]);
                        throw;
                    }
                }, () => AcquirePropertyStringOwners(owners), text =>
                {
                    if (scalarType == typeof(decimal) && !ranged) return null;
                    return NativeNumericInput.StepText(scalarType, Coordinate(first.Node.Retrieve(), 0), mixed, range, ref text) ? text : null;
                }, Format, externalText: ranged && display != null && (mixed || !Equals(display, coordinate)) ? Convert.ToString(display, CultureInfo.InvariantCulture) : null,
                    externalActive: sliderActive, externalCommit: sliderCommit,
                    onCancelled: error => ClearCancelledPropertyDraft(error, feedback));
            }
            catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); SetPropertyDraftDiagnostic(error); }
            PropertyConversionFeedback.Draw(feedback); Reset();
        }
    }
}
