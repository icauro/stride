using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal static class NativeNumericAxes
{
    internal static bool IsReadOnly(INodePresenter parent, int axis)
    {
        var names = Names(parent.Type);
        if (axis < 0 || axis >= names.Count || parent.TryGetChild(names[axis]) is not { } child) return false;
        for (INodePresenter? current = child; current != null; current = current.Parent)
            if (current.IsReadOnly) return true;
        return false;
    }
    internal static void AssertEditable(INodePresenter parent, object? previous, object? proposed)
    {
        // Native ColorPicker and RotationEditor bind the whole parent value.
        var type = Nullable.GetUnderlyingType(parent.Type) ?? parent.Type;
        if (type == typeof(Color) || type == typeof(Color3) || type == typeof(Color4)) return;
        var names = Names(parent.Type);
        for (var axis = 0; axis < names.Count; axis++)
            if (IsReadOnly(parent, axis) && !Equals(Read(previous, names[axis]), Read(proposed, names[axis])))
                throw new InvalidOperationException("The native coordinate is read-only.");
    }
    private static object? Read(object? value, string name)
        => value != null && TypeDescriptorFactory.Default.Find(value.GetType()) is ObjectDescriptor descriptor
            ? descriptor.Members.FirstOrDefault(member => member.Name == name)?.Get(value) : null;
    private static readonly string[] xy = ["X", "Y"];
    private static readonly string[] xyz = ["X", "Y", "Z"];
    private static readonly string[] xyzw = ["X", "Y", "Z", "W"];
    private static readonly string[] rgb = ["R", "G", "B"];
    private static readonly string[] rgba = ["R", "G", "B", "A"];
    private static readonly string[] rectangle = ["X", "Y", "Width", "Height"];
    private static readonly string[] matrix = Enumerable.Range(0, 16).Select(index => "M" + (index / 4 + 1) + (index % 4 + 1)).ToArray();
    private static readonly string[] thickness = ["Left", "Right", "Top", "Bottom", "Back", "Front"];
    internal static IReadOnlyList<string> Names(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(Vector2) || type == typeof(Int2) ? xy
            : type == typeof(Vector3) || type == typeof(Int3) ? xyz
            : type == typeof(Vector4) || type == typeof(Int4) || type == typeof(UInt4) ? xyzw
            : type == typeof(Color3) ? rgb : type == typeof(Color4) || type == typeof(Color) ? rgba
            : type == typeof(Rectangle) || type == typeof(RectangleF) ? rectangle
            : type == typeof(Matrix) ? matrix : type == typeof(Stride.UI.Thickness) ? thickness : [];
    }
    internal static object? Normalize(object? value, object? previous, IReadOnlyList<PropertyNumericRange?> ranges)
    {
        float Float(int axis, float next, float? old) => old == next || ranges.ElementAtOrDefault(axis) is not { } range ? next : (float)range.Normalize(next)!;
        int Int(int axis, int next, int? old) => old == next || ranges.ElementAtOrDefault(axis) is not { } range ? next : (int)range.Normalize(next)!;
        byte Byte(int axis, byte next, byte? old) => old == next || ranges.ElementAtOrDefault(axis) is not { } range ? next : (byte)range.Normalize(next)!;
        if (value is Color colorValue)
        {
            var old = previous as Color?;
            return new Color(Byte(0, colorValue.R, old?.R), Byte(1, colorValue.G, old?.G), Byte(2, colorValue.B, old?.B), Byte(3, colorValue.A, old?.A));
        }
        if (value is Matrix matrixValue)
        {
            var old = previous as Matrix?;
            for (var axis = 0; axis < 16; axis++) matrixValue[axis] = Float(axis, matrixValue[axis], old?[axis]);
            return matrixValue;
        }
        if (value is Rectangle rectangleValue)
        {
            var old = previous as Rectangle?;
            return new Rectangle(Int(0, rectangleValue.X, old?.X), Int(1, rectangleValue.Y, old?.Y),
                Int(2, rectangleValue.Width, old?.Width), Int(3, rectangleValue.Height, old?.Height));
        }
        if (value is RectangleF rectangleFloat)
        {
            var old = previous as RectangleF?;
            return new RectangleF(Float(0, rectangleFloat.X, old?.X), Float(1, rectangleFloat.Y, old?.Y),
                Float(2, rectangleFloat.Width, old?.Width), Float(3, rectangleFloat.Height, old?.Height));
        }
        if (value is Stride.UI.Thickness thicknessValue)
        {
            var old = previous as Stride.UI.Thickness?;
            thicknessValue.Left = Float(0, thicknessValue.Left, old?.Left); thicknessValue.Right = Float(1, thicknessValue.Right, old?.Right);
            thicknessValue.Top = Float(2, thicknessValue.Top, old?.Top); thicknessValue.Bottom = Float(3, thicknessValue.Bottom, old?.Bottom);
            thicknessValue.Back = Float(4, thicknessValue.Back, old?.Back); thicknessValue.Front = Float(5, thicknessValue.Front, old?.Front);
            return thicknessValue;
        }
        return VectorPropertyNumericMetadata.Normalize(value, previous, ranges);
    }
}
