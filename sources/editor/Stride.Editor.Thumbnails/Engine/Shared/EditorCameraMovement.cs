using Stride.Core.Mathematics;

namespace Stride.Engine.Design;

/// <summary>Original editor camera speed table and index conversion, independent of presentation.</summary>
public static class EditorCameraMovement
{
    public static readonly float[] AvailableSpeeds =
    [ .05f, .1f, .5f, 1f, 1.5f, 2.5f, 5f, 10f, 15f, 30f, 45f, 60f, 80f, 100f, 130f, 180f, 250f, 400f, 500f, 700f, 1000f ];

    public static int FindIndex(float value, float[]? speeds = null)
    {
        speeds ??= AvailableSpeeds;
        for (var i = 0; i < speeds.Length; i++) if (MathUtil.NearEqual(value, speeds[i])) return i;
        return 2;
    }
    public static float ValueAt(int index, float[]? speeds = null)
    {
        speeds ??= AvailableSpeeds;
        return speeds[MathUtil.Clamp(index, 0, speeds.Length - 1)];
    }
}
