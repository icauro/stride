using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

// Original RotationEditor decomposition and PDX-1779 retained Euler coordinates.
internal sealed class NativeRotationEditorState
{
    private Vector3 radians;
    private bool hasComponents;
    internal NativeRotationEditorState Capture() => new() { radians = radians, hasComponents = hasComponents };
    internal void Restore(NativeRotationEditorState snapshot)
    {
        radians = snapshot.radians;
        hasComponents = snapshot.hasComponents;
    }
    internal Vector3 Read(Quaternion value)
    {
        if (!hasComponents || Compose() != value)
        {
            var matrix = Matrix.RotationQuaternion(value);
            matrix.Decompose(out radians.Y, out radians.X, out radians.Z);
            hasComponents = true;
        }
        return new(Display(radians.X), Display(radians.Y), Display(radians.Z));
    }
    internal Quaternion ReplaceAxis(Quaternion current, int axis, float degrees)
    {
        Read(current);
        radians[axis] = MathUtil.DegreesToRadians(MathF.Round(degrees, 3));
        return Compose();
    }
    internal Quaternion ReplaceComponents(Quaternion current, Vector3 degrees, int changedAxes)
    {
        Read(current);
        for (var axis = 0; axis < 3; axis++)
            if ((changedAxes & (1 << axis)) != 0) radians[axis] = MathUtil.DegreesToRadians(MathF.Round(degrees[axis], 3));
        return Compose();
    }
    private Quaternion Compose() => Quaternion.RotationYawPitchRoll(radians.Y, radians.X, radians.Z);
    private static float Display(float angle)
    {
        var degrees = MathF.Round(MathUtil.RadiansToDegrees(angle), 3);
        return degrees == 0 ? 0 : degrees;
    }
}
