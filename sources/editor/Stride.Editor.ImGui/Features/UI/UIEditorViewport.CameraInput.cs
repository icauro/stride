using Stride.Core.Mathematics;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorViewport
{
    internal void ApplyCameraInput(NVector2 normalizedMotion, float wheel, bool panning, bool shift, float speed, float sceneUnit)
    {
        var elapsed = (float)game.UpdateTime.Elapsed.TotalSeconds;
        if (!float.IsFinite(elapsed) || elapsed < 0 || !float.IsFinite(speed) || !float.IsFinite(sceneUnit)) return;
        var translation = speed * sceneUnit * elapsed * (shift ? 10 : 1);
        cameraMoveSpeed = speed; cameraSceneUnit = sceneUnit;
        if (panning)
        {
            var rotation = Matrix.RotationYawPitchRoll(cameraPitchYaw.Y, cameraPitchYaw.X, 0);
            var forward = Vector3.TransformNormal(-Vector3.UnitZ, rotation);
            var up = Vector3.TransformNormal(Vector3.UnitY, rotation);
            var right = Vector3.Cross(forward, up);
            cameraPosition -= right * normalizedMotion.X * 100 * translation;
            cameraPosition += up * normalizedMotion.Y * 100 * translation;
            UpdateCamera();
        }
        else if (Math.Abs(wheel) > MathUtil.ZeroTolerance)
            SetOrthographicSize(camera.OrthographicSize - translation * 12 * wheel);
    }
}
