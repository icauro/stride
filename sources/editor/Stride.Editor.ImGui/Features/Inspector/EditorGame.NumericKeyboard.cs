using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static Type PropertyCoordinateType(Type type, int axis) => axis < 0 ? type
        : type == typeof(UInt4) ? typeof(uint)
        : type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(Rectangle) ? typeof(int) : typeof(float);
    private static PropertyNumericRange? FieldCoordinateRange(ComponentField field, int axis)
        => axis < 0 ? field.EffectiveNumericRange : field.AxisNumericRanges.ElementAtOrDefault(axis);
    private static bool StepPropertyText(IReadOnlyList<ComponentField> fields, int axis, object? value, bool mixed, ref string text)
    {
        var type = PropertyCoordinateType(fields[0].Type, axis);
        if (!NativeNumericInput.TryGesture(type, null, out _)) return false;
        var range = fields.Count == 1 ? FieldCoordinateRange(fields[0], axis)
            : NativeNumericInput.Merge(type, fields.Select(field => FieldCoordinateRange(field, axis)));
        return NativeNumericInput.StepText(type, value, mixed, range, ref text);
    }
}
