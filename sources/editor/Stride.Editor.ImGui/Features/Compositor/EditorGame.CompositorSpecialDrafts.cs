using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool UsesCompositorRetainedSpecial(Type type)
        => type == typeof(AngleSingle) || type == typeof(Guid) || type == typeof(char);

    private void DrawCompositorRetainedSpecial(AssetMetadataDocument source, IReadOnlyList<CompositorProperty> properties,
        CompositorAggregateProperty? aggregate = null)
    {
        var target = CaptureCompositorDraftTarget(source, properties, aggregate);
        var first = target.Fields[0];
        bool Editable() => target.Editable() && target.Read().Take(target.Fields.Length).All(value => value != null);
        void Assign(object value) => target.Apply(_ => value);
        if (first.Type == typeof(AngleSingle))
            DrawRetainedAngle(first.Accessor.Node, target.Read, Editable, angle => Assign(angle), target.Acquire, target.Fields.Length);
        else if (first.Type == typeof(Guid))
            DrawRetainedGuid(first.Accessor.Node, target.Read, Editable, guid => Assign(guid), target.Acquire, target.Fields.Length);
        else
            DrawRetainedCharacter(first.Accessor.Node, target.Read, Editable, character => Assign(character), target.Acquire, target.Fields.Length);
    }
}
