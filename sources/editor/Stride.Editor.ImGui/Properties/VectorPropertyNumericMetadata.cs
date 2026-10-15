using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal static class VectorPropertyNumericMetadata
{
    internal static IReadOnlyList<PropertyNumericRange?> Create(IMemberNode node)
    {
        return Create(Nullable.GetUnderlyingType(node.Type) ?? node.Type, node.MemberDescriptor.MemberInfo);
    }

    internal static IReadOnlyList<PropertyNumericRange?> Create(Type type, System.Reflection.MemberInfo? member = null, DataMemberRangeAttribute? transferredRange = null)
    {
        var rectangle = type == typeof(Rectangle) || type == typeof(RectangleF);
        var integral = type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(Rectangle);
        var packedColor = type == typeof(Color);
        var color = packedColor || type == typeof(Color3) || type == typeof(Color4);
        var unsigned = type == typeof(UInt4);
        var dimensions = type == typeof(Vector2) || type == typeof(Int2) ? 2
            : type == typeof(Vector3) || type == typeof(Int3) || type == typeof(Color3) ? 3
            : type == typeof(Vector4) || type == typeof(Int4) || type == typeof(Color4) || packedColor || unsigned || rectangle ? 4 : 0;
        if (dimensions == 0) return [];
        var registry = TypeDescriptorFactory.Default.AttributeRegistry;
        var memberRange = member == null || color || unsigned ? null : transferredRange ?? registry.GetAttribute<DataMemberRangeAttribute>(member);
        var ranges = new PropertyNumericRange?[dimensions];
        for (var axis = 0; axis < dimensions; axis++)
        {
            var axisMember = type.GetField(rectangle ? new[] { "X", "Y", "Width", "Height" }[axis] : (color ? "RGBA" : "XYZW")[axis].ToString())!;
            var metadata = registry.GetAttribute<DataMemberRangeAttribute>(axisMember) ?? memberRange;
            if (metadata != null) ranges[axis] = new PropertyNumericRange(metadata, packedColor ? typeof(byte) : unsigned ? typeof(uint) : integral ? typeof(int) : typeof(float));
            else if (packedColor) ranges[axis] = new(new PropertyNumericMetadata(
                Convert.ToDouble(Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters.NumericValueNodeUpdater.GetMinimum(typeof(byte))),
                Convert.ToDouble(Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters.NumericValueNodeUpdater.GetMaximum(typeof(byte))), 1, 5, 0), typeof(byte));
        }
        return ranges;
    }

    internal static object? Normalize(object? value, object? previous, IReadOnlyList<PropertyNumericRange?> ranges)
    {
        if (ranges.Count == 0) return value;
        float Float(int axis, float proposed, float? old) => old == proposed || ranges[axis] == null ? proposed : (float)ranges[axis]!.Normalize(proposed)!;
        int Int(int axis, int proposed, int? old) => old == proposed || ranges[axis] == null ? proposed : (int)ranges[axis]!.Normalize(proposed)!;
        uint UInt(int axis, uint proposed, uint? old) => old == proposed || ranges[axis] == null ? proposed : (uint)ranges[axis]!.Normalize(proposed)!;
        return value switch
        {
            UInt4 v => new UInt4(UInt(0, v.X, (previous as UInt4?)?.X), UInt(1, v.Y, (previous as UInt4?)?.Y), UInt(2, v.Z, (previous as UInt4?)?.Z), UInt(3, v.W, (previous as UInt4?)?.W)),
            Color3 v => new Color3(Float(0, v.R, (previous as Color3?)?.R), Float(1, v.G, (previous as Color3?)?.G), Float(2, v.B, (previous as Color3?)?.B)),
            Color4 v => new Color4(Float(0, v.R, (previous as Color4?)?.R), Float(1, v.G, (previous as Color4?)?.G), Float(2, v.B, (previous as Color4?)?.B), Float(3, v.A, (previous as Color4?)?.A)),
            Vector2 v => new Vector2(Float(0, v.X, (previous as Vector2?)?.X), Float(1, v.Y, (previous as Vector2?)?.Y)),
            Vector3 v => new Vector3(Float(0, v.X, (previous as Vector3?)?.X), Float(1, v.Y, (previous as Vector3?)?.Y), Float(2, v.Z, (previous as Vector3?)?.Z)),
            Vector4 v => new Vector4(Float(0, v.X, (previous as Vector4?)?.X), Float(1, v.Y, (previous as Vector4?)?.Y), Float(2, v.Z, (previous as Vector4?)?.Z), Float(3, v.W, (previous as Vector4?)?.W)),
            Int2 v => new Int2(Int(0, v.X, (previous as Int2?)?.X), Int(1, v.Y, (previous as Int2?)?.Y)),
            Int3 v => new Int3(Int(0, v.X, (previous as Int3?)?.X), Int(1, v.Y, (previous as Int3?)?.Y), Int(2, v.Z, (previous as Int3?)?.Z)),
            Int4 v => new Int4(Int(0, v.X, (previous as Int4?)?.X), Int(1, v.Y, (previous as Int4?)?.Y), Int(2, v.Z, (previous as Int4?)?.Z), Int(3, v.W, (previous as Int4?)?.W)),
            _ => value,
        };
    }
}
