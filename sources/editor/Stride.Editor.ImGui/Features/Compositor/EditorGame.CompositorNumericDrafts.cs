using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCompositorRetainedNumeric(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties,
        CompositorAggregateProperty? aggregate = null)
    {
        var target = CaptureCompositorDraftTarget(source, properties, aggregate);
        var fields = target.Fields; var first = fields[0]; var type = first.Type;
        var values = target.Read(); var value = values[0]; var mixed = values.Take(fields.Length).Any(current => !Equals(current, value));
        PropertyNumericRange? Range() => NativeNumericInput.Merge(type, fields.Select(field => field.Range));
        var ranges = fields.Select(field => field.Range).ToArray();
        object? display = value; var sliderActive = false; var sliderCommit = false;
        if (fields.All(field => field.UsesRangedNumericTemplate && NativeRangedNumericControl.Matches(field.DeclaredType, field.Range)))
        {
            using var disabled = ImGuiScopes.Disabled(!target.Editable());
            sliderCommit = NativeRangedNumericControl.Draw(first.Accessor.Node, type, value, mixed, ranges, UiScale(1), out display, out _);
            sliderActive = Gui.IsItemActive();
        }
        var feedback = Gui.GetID("##Value");
        try
        {
            PropertyGrid.Strings.Draw(first.Accessor.Node, mixed, target.Editable, target.Read,
                text =>
                {
                    var number = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, type, Range()));
                    target.Apply(_ => number);
                }, target.Acquire,
                step: text =>
                {
                    if (!target.Editable()) return null;
                    return NativeNumericInput.StepText(type, target.Read()[0], mixed, Range(), ref text) ? text : null;
                }, format: raw => NativeNumericText.Format(raw, type, Range()),
                externalText: sliderActive || sliderCommit ? NativeNumericText.Format(display, type, Range()) : null,
                externalActive: sliderActive, externalCommit: sliderCommit);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
    }
}
