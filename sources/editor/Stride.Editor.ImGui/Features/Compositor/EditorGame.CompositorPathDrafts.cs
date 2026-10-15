namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCompositorRetainedPath(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties,
        CompositorAggregateProperty? aggregate = null)
    {
        var target = CaptureCompositorDraftTarget(source, properties, aggregate);
        var first = target.Fields[0];
        DrawRetainedPath(source, first.Accessor.Node, first.Type, target.Editable, target.Read,
            path => target.Apply(_ => path), target.Fields.Length, target.Acquire);
    }
}
