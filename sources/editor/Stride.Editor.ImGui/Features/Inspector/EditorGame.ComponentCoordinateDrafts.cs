using System.Globalization;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawComponentCoordinateDraft(ComponentField field, int axis, string control, NativeRotationEditorState? rotation = null, bool inherited = false)
    {
        var owner = document!;
        var type = PropertyCoordinateType(field.Type, axis);
        var range = rotation == null ? FieldCoordinateRange(field, axis) : null;
        object? Coordinate(object? value) => rotation != null && value is Quaternion quaternion
            ? rotation.Read(quaternion)[axis] : CommonCoordinate(value, axis);
        bool Editable() => ReferenceEquals(document, owner) && !IsAuthoringPipelineUpdating
            && (inherited ? owner.CanOverrideInheritedComponent(field) : owner.CanEditComponentValue(field))
            && field.Component.Entity is { } entity && owner.IsSelected(entity.Id)
            && (rotation != null || !field.IsAxisReadOnly(axis));
        var feedback = Gui.GetID(control);
        try
        {
            PropertyGrid.Strings.Draw(field.Node, false, Editable, () => [field.Node.Retrieve()], text =>
            {
                var snapshot = rotation?.Capture();
                try
                {
                    var number = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, type, range));
                    if (number is float scalar && !float.IsFinite(scalar)) throw new ArgumentException("The coordinate must be finite.");
                    var current = field.Node.Retrieve();
                    var next = rotation != null && current is Quaternion quaternion
                        ? rotation.ReplaceAxis(quaternion, axis, (float)number)
                        : WithCommonCoordinate(current, field.Type, axis, number);
                    if (inherited) owner.EditInheritedScalar(field, next);
                    else { owner.UpdateComponent(field, next); owner.EndComponentEdit(); }
                    componentErrors.Remove(field);
                }
                catch
                {
                    if (snapshot != null) rotation!.Restore(snapshot);
                    throw;
                }
            }, () => AcquirePropertyStringOwners([owner]), text =>
                NativeNumericInput.StepText(type, Coordinate(field.Node.Retrieve()), false, range, ref text) ? text : null,
                value => Coordinate(value) is float number
                    ? number.ToString("F" + (range?.DecimalPlaces ?? 3), CultureInfo.InvariantCulture)
                    : Convert.ToString(Coordinate(value), CultureInfo.InvariantCulture) ?? "", controlLabel: control);
        }
        catch (Exception error)
        {
            PropertyConversionFeedback.Fail(feedback, error); componentErrors[field] = error.Message; statusMessage = error.Message;
        }
        PropertyConversionFeedback.Draw(feedback);
    }
}
