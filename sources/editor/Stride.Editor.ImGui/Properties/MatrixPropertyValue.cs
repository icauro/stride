using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal static class MatrixPropertyValue
{
    internal static object? Normalize(object? value, object? previous)
    {
        if (value is not Matrix matrix) return value;
        var old = previous as Matrix?;
        for (var index = 0; index < 16; index++)
        {
            if (!float.IsFinite(matrix[index])) throw new ArgumentException("Numeric property values must be finite.");
            if (old == null || old.Value[index] != matrix[index]) matrix[index] = MathF.Round(matrix[index], 3);
        }
        return matrix;
    }
}
