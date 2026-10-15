using Stride.Animations;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal enum CurveRotationDisplayMode { Quaternion, Euler, AxisAngle }

internal sealed class RotationCurveDisplay(IComputeCurve<Quaternion> curve)
{
    internal IComputeCurve<Quaternion> Curve => curve;
    internal CurveRotationDisplayMode Mode = CurveRotationDisplayMode.Euler;
    private CurveRotationDisplayMode sampledMode = CurveRotationDisplayMode.Euler;
    private readonly Dictionary<float, Vector4> samples = [];
    internal void PrepareSampling()
    {
        curve.UpdateChanges(); samples.Clear(); sampledMode = Mode;
    }
    internal float Sample(float key, int channel)
    {
        if (sampledMode != Mode) { samples.Clear(); sampledMode = Mode; }
        if (!samples.TryGetValue(key, out var sample)) samples.Add(key, sample = Evaluate(key));
        return channel switch { 0 => sample.X, 1 => sample.Y, 2 => sample.Z, _ => sample.W };
    }
    private Vector4 Evaluate(float key)
    {
        var value = curve.Evaluate(key);
        if (Mode == CurveRotationDisplayMode.Quaternion)
            return new(value.X, value.Y, value.Z, value.W);
        Vector3 vector;
        if (Mode == CurveRotationDisplayMode.AxisAngle)
        {
            vector = value.Axis; vector.Normalize();
            return new(vector.X, vector.Y, vector.Z, MathUtil.RadiansToDegrees(value.Angle));
        }
        Matrix.RotationQuaternion(ref value, out var matrix);
        // RotationCurveViewModel's native XYZ decomposition, including its pole policy.
        var rotY = MathUtil.IsOne(Math.Abs(matrix.M13)) ? Math.Acos(Math.Max(Math.Abs(matrix.M11), Math.Abs(matrix.M33))) : Math.Asin(-matrix.M13);
        vector.Y = (float)(MathF.Sign(-matrix.M13) * rotY);
        if (Math.Cos(rotY) > MathUtil.ZeroTolerance)
        {
            vector.Z = MathF.Atan2(matrix.M12, matrix.M11);
            vector.X = MathF.Atan2(matrix.M23, matrix.M33);
        }
        else
        {
            vector.Z = matrix.M13 >= 0 ? MathF.Atan2(-matrix.M32, matrix.M22) : -MathF.Atan2(-matrix.M32, matrix.M22);
            vector.X = 0;
        }
        return new(MathUtil.RadiansToDegrees(vector.X), MathUtil.RadiansToDegrees(vector.Y), MathUtil.RadiansToDegrees(vector.Z), float.NaN);
    }
}

internal sealed class RotationCurveTrack(RotationCurveDisplay display, string name, int channel) : ICurveTrack
{
    internal RotationCurveDisplay Display => display;
    public string Name => name;
    public object CurveIdentity => display.Curve;
    public int Channel => channel;
    public IEnumerable<float> SamplingKeys => CurveSamplingKeys.Enumerate(display.Curve);
    public bool Editable => false;
    public IReadOnlyList<CurvePoint> ReadPoints() => [];
    public void PrepareSampling() => display.PrepareSampling();
    public float Sample(float key) => display.Sample(key, channel);
    public void Add(float key, float value) => throw new InvalidOperationException("The sampled curve is read-only.");
    public void Edit(IReadOnlyList<(object Point, float Key, float Value)> edits) => throw new InvalidOperationException("The sampled curve is read-only.");
    public void SetTangent(IEnumerable<object> points, AnimationKeyTangentType tangent) => throw new InvalidOperationException("The sampled curve is read-only.");
    public void Delete(IEnumerable<object> points) => throw new InvalidOperationException("The sampled curve is read-only.");
    public string Copy(IEnumerable<object> points) => "";
    public bool CanPaste(string text) => false;
    public IReadOnlyList<object> Paste(string text) => throw new InvalidOperationException("The sampled curve is read-only.");
}
