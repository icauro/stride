using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal static class CameraLens
{
    public static bool Valid(float fov, float near, float far) => float.IsFinite(fov) && fov > 0 && fov < 180
        && float.IsFinite(near) && near > 0 && float.IsFinite(far) && far > near
        && Matrix.PerspectiveFovRH(MathUtil.DegreesToRadians(fov), 1, near, far).ToArray().All(float.IsFinite);
}
