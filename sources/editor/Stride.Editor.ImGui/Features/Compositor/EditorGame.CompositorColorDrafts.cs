using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool UsesCompositorRetainedColor(Type type)
        => type == typeof(Color) || type == typeof(Color3) || type == typeof(Color4);

    private void DrawCompositorRetainedColor(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties,
        CompositorAggregateProperty? aggregate = null)
    {
        var target = CaptureCompositorDraftTarget(source, properties, aggregate);
        var first = target.Fields[0];
        bool Editable() => target.Editable() && target.Read().Take(target.Fields.Length).All(value => value != null);
        DrawRetainedColor(first.Accessor.Node, first.Type, target.Read, Editable,
            replacement => target.Apply(field => replacement(source.CompositorPropertyValue(field))), target.Acquire,
            axisEditable: axis => target.Fields.All(field => !source.CompositorAxisReadOnly(field, axis)),
            valueCount: target.Fields.Length);
    }
}
