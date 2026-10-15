using System.Globalization;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly PropertyNumericRange retainedAngleRange = new(new PropertyNumericMetadata(DecimalPlaces: 3), typeof(double));
    private void DrawRetainedAngle(object identity, Func<object?[]> read, Func<bool> canEdit, Action<AngleSingle> assign,
        Func<IDisposable> acquire, int valueCount = 1)
    {
        var values = read(); var mixed = values.Take(valueCount).Any(value => !Equals(value, values[0]));
        double Degrees(object? value) => value is AngleSingle angle ? Math.Round((double)angle.Degrees, 3) : 0;
        var feedback = Gui.GetID("##Angle"); Gui.SetNextItemWidth(-1);
        try
        {
            PropertyGrid.Strings.Draw(identity, mixed, () => !IsAuthoringPipelineUpdating && canEdit(), read, text =>
            {
                var next = PropertyConversionFeedback.Convert(feedback, () =>
                {
                    var degrees = (double)NativeNumericText.Parse(text, typeof(double), retainedAngleRange);
                    var angle = new AngleSingle((float)degrees, AngleType.Degree);
                    PropertyValueValidation.Normalize(angle, null);
                    return angle;
                });
                assign(next);
            }, acquire, text => NativeNumericInput.StepText(typeof(double), Degrees(read()[0]), mixed, retainedAngleRange, ref text) ? text : null,
                raw => Degrees(raw).ToString(CultureInfo.InvariantCulture), controlLabel: "##Angle");
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Angle in degrees", "ToolTip"));
        PropertyConversionFeedback.Draw(feedback);
    }
    private void DrawComponentRetainedAngle(ComponentField field)
    {
        var owner = document!; Gui.PushID(field.Name);
        try
        {
            if (!BeginDocumentedComponentRow(field)) return;
            try
            {
                DrawRetainedAngle(field.Node, () => [field.Node.Retrieve()],
                    () => ReferenceEquals(document, owner) && owner.CanEditComponentValue(field),
                    angle => { owner.UpdateComponent(field, angle); owner.EndComponentEdit(); componentErrors.Remove(field); },
                    () => AcquirePropertyStringOwners([owner]));
                if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
    private void DrawRetainedCombinedVirtualAngle(IReadOnlyList<ComponentVirtualOwner> source)
    {
        var entries = source.ToArray(); var first = entries[0].Node;
        DrawRetainedAngle(first, () => entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray(),
            () => entries.All(entry => entry.Document.CanEditComponentVirtual(entry.Node)),
            angle => ApplyCombinedComponentVirtualCore(entries, (_, _) => angle),
            () => AcquireRetainedCombinedVirtualTargets(entries), entries.Length);
    }
}
