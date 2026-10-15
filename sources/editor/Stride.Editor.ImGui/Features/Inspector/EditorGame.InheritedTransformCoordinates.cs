using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawInheritedTransformCoordinate(ComponentField field, int axis, uint? rotationView = null)
    {
        var owner = document!;
        var rotation = rotationView is { } view ? RotationEditor(field.Node, view) : null;
        object? Coordinate(object? value) => rotation != null && value is Quaternion quaternion
            ? rotation.Read(quaternion)[axis] : CommonCoordinate(value, axis);
        bool Editable() => ReferenceEquals(document, owner) && owner.CanOverrideInheritedComponent(field)
            && field.Component.Entity is { } entity && owner.IsSelected(entity.Id)
            && (rotation != null || !field.IsAxisReadOnly(axis));
        var feedback = Gui.GetID("##Value");
        Gui.SetNextItemWidth(-1);
        try
        {
            PropertyGrid.Strings.Draw(field.Node, false, Editable, () => [field.Node.Retrieve()], text =>
            {
                var snapshot = rotation?.Capture();
                try
                {
                    var degrees = PropertyConversionFeedback.Convert(feedback,
                        () => (float)NativeNumericText.Parse(text, typeof(float), rotation != null ? null : FieldCoordinateRange(field, axis)));
                    if (!float.IsFinite(degrees)) throw new ArgumentException("The coordinate must be finite.");
                    var current = field.Node.Retrieve();
                    var next = rotation != null && current is Quaternion quaternion ? rotation.ReplaceAxis(quaternion, axis, degrees)
                        : WithCommonCoordinate(current, field.Type, axis, degrees);
                    owner.EditInheritedScalar(field, next); componentErrors.Remove(field);
                }
                catch
                {
                    if (snapshot != null) rotation!.Restore(snapshot);
                    throw;
                }
            }, () => AcquirePropertyStringOwners([owner]), text =>
            {
                var range = rotation != null ? null : FieldCoordinateRange(field, axis);
                return NativeNumericInput.StepText(typeof(float), Coordinate(field.Node.Retrieve()), false, range, ref text) ? text : null;
            }, value => Convert.ToString(Coordinate(value), CultureInfo.InvariantCulture) ?? "");
        }
        catch (Exception error)
        {
            PropertyConversionFeedback.Fail(feedback, error); componentErrors[field] = error.Message; statusMessage = error.Message;
        }
        PropertyConversionFeedback.Draw(feedback); DrawInheritedResetAction(field);
    }
}
