using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCompositorRetainedString(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties,
        CompositorAggregateProperty? aggregate = null)
    {
        var target = CaptureCompositorDraftTarget(source, properties, aggregate);
        var fields = target.Fields;
        var values = target.Read(); var mixed = values.Take(fields.Length).Any(value => !Equals(value, values[0]));
        var feedback = Gui.GetID("##Value");
        try
        {
            PropertyGrid.Strings.Draw(fields[0].Accessor.Node, mixed, target.Editable, target.Read,
                replacement => target.Apply(_ => replacement), target.Acquire);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
    }
}
