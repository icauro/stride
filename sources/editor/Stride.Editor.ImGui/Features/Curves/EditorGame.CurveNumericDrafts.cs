using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly PropertyNumericRange curveKeyNumericRange = new(new PropertyNumericMetadata(null, null, .001, .01, 6), typeof(float));
    private static readonly PropertyNumericRange curveValueNumericRange = new(new PropertyNumericMetadata(null, null, 1, 10, 6), typeof(float));
    private void DrawCurveNumericDraft(CurvePointNumericTarget target, bool key, Func<bool> currentSelection)
    {
        var range = key ? curveKeyNumericRange : curveValueNumericRange;
        var label = EditorLocalization.Widget(key ? "Key" : "Value");
        bool Editable() => !IsAuthoringPipelineUpdating && currentSelection() && target.CanEdit();
        object?[] Read() => [.. target.Read(), currentSelection()];
        Gui.SetNextItemWidth(UiScale(80));
        var feedback = Gui.GetID(label);
        try
        {
            PropertyGrid.Strings.Draw(target.Identity, false, Editable, Read, text =>
            {
                var number = (float)NativeNumericText.Parse(text, typeof(float), range);
                if (!float.IsFinite(number)) throw new FormatException("The control point value must be finite.");
                if (!Editable()) throw new InvalidOperationException("The control point selection changed.");
                target.Commit(number);
            }, () =>
            {
                if (!Editable()) throw new InvalidOperationException("The control point owner changed.");
                return new PropertyStringOwners([target.Owner.AcquireView()], [target.Owner.Document]);
            }, text =>
            {
                var stepped = text;
                return NativeNumericInput.StepText(typeof(float), target.Read()[0], false, range, ref stepped) ? stepped : null;
            }, format: value => NativeNumericText.Format(value, typeof(float), range), controlLabel: label, refreshAfterCommit: false);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
    }
}
