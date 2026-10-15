using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal static class PropertyValueValidation
{
    public static object? Normalize(object? value, PropertyNumericRange? range)
    {
        if (value is float single && !float.IsFinite(single) || value is double number && !double.IsFinite(number)
            || value is Color3 rgb && (!float.IsFinite(rgb.R) || !float.IsFinite(rgb.G) || !float.IsFinite(rgb.B))
            || value is Color4 rgba && (!float.IsFinite(rgba.R) || !float.IsFinite(rgba.G) || !float.IsFinite(rgba.B) || !float.IsFinite(rgba.A))
            || value is Vector2 xy && (!float.IsFinite(xy.X) || !float.IsFinite(xy.Y))
            || value is Vector3 xyz && (!float.IsFinite(xyz.X) || !float.IsFinite(xyz.Y) || !float.IsFinite(xyz.Z))
            || value is Vector4 xyzw && (!float.IsFinite(xyzw.X) || !float.IsFinite(xyzw.Y) || !float.IsFinite(xyzw.Z) || !float.IsFinite(xyzw.W))
            || value is Quaternion rotation && (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
            || value is AngleSingle angle && !float.IsFinite(angle.Radians))
            throw new ArgumentException("Numeric property values must be finite.");
        return range?.Normalize(value) ?? value;
    }
}
