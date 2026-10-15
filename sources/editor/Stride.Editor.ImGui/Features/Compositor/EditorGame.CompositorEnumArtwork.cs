namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawCompositorEnumArtwork(CompositorProperty property, object value, out object? updated)
    {
        DrawCompositorLabel(property);
        return NativeEnumImageControl.Draw(icons!, property.Accessor.Node, property.Type, value, false,
            Enum.GetValues(property.Type).Cast<Enum>(), UiScale(1), out updated);
    }
    private void DrawCompositorAggregateEnumArtwork(AssetMetadataDocument document, CompositorAggregateProperty property)
    {
        var first = property.First;
        if (NativeEnumImageControl.Draw(icons!, first.Accessor.Node, first.Type, property.Value, property.IsMixed,
            Enum.GetValues(first.Type).Cast<Enum>(), UiScale(1), out var updated))
            ExecuteCompositorCommand(document, () => document.EditCompositorAggregate(property, _ => updated));
    }
}
