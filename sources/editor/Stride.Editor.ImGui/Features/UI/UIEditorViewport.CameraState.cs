using Stride.Assets.Presentation.SceneEditor;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Processors;

namespace Stride.Editor.ImGui;

internal sealed record UICameraView(Vector3 Position, Vector2 PitchYaw, CameraProjectionMode Projection,
    float Size, float Fov, float Near, float Far, float Speed, float SceneUnit)
{
    internal bool Valid => float.IsFinite(Position.X) && float.IsFinite(Position.Y) && float.IsFinite(Position.Z)
        && float.IsFinite(PitchYaw.X) && float.IsFinite(PitchYaw.Y) && Enum.IsDefined(Projection)
        && float.IsFinite(Size) && Size > 0 && float.IsFinite(Fov) && Fov > 0 && Fov < 180
        && float.IsFinite(Near) && Near > 0 && float.IsFinite(Far) && Far > Near
        && float.IsFinite(Speed) && float.IsFinite(SceneUnit) && SceneUnit > MathUtil.ZeroTolerance;
    internal static UICameraView FromNative(SceneSettingsData value) => new(value.CamPosition, value.CamPitchYaw,
        value.CamProjection, value.CamOrthographicSize, value.CamVerticalFieldOfView, value.CamNearClipPlane,
        value.CamFarClipPlane, value.CamMoveSpeed, value.SceneUnit <= MathUtil.ZeroTolerance ? 1 : value.SceneUnit);
    internal void WriteNative(SceneSettingsData value)
    {
        value.CamPosition = Position; value.CamPitchYaw = PitchYaw; value.CamProjection = Projection;
        value.CamOrthographicSize = Size; value.CamVerticalFieldOfView = Fov; value.CamNearClipPlane = Near;
        value.CamFarClipPlane = Far; value.CamMoveSpeed = Speed; value.SceneUnit = SceneUnit;
    }
}

internal sealed partial class UIEditorViewport
{
    private Vector3 cameraPosition = new(0, 0, 500);
    private Vector2 cameraPitchYaw;
    private float cameraMoveSpeed = 3, cameraSceneUnit = 1;
    internal void ResetCamera()
    {
        cameraPosition = new Vector3(0, 0, 500);
        cameraPitchYaw = Vector2.Zero;
        camera.OrthographicSize = CameraComponent.DefaultOrthographicSize;
        UpdateCamera();
    }
    internal UICameraView CaptureCameraView() => new(cameraPosition, cameraPitchYaw, camera.Projection,
        camera.OrthographicSize, camera.VerticalFieldOfView, camera.NearClipPlane, camera.FarClipPlane, cameraMoveSpeed, cameraSceneUnit);
    internal void RestoreCameraView(UICameraView value)
    {
        if (!value.Valid) throw new InvalidOperationException("The saved UI camera settings are invalid.");
        cameraPosition = value.Position;
        cameraPitchYaw = new Vector2(MathUtil.Clamp(value.PitchYaw.X, -MathUtil.PiOverTwo, MathUtil.PiOverTwo), value.PitchYaw.Y);
        camera.Projection = value.Projection; camera.OrthographicSize = value.Size; camera.VerticalFieldOfView = value.Fov;
        camera.NearClipPlane = value.Near; camera.FarClipPlane = value.Far;
        cameraMoveSpeed = value.Speed; cameraSceneUnit = value.SceneUnit;
        UpdateCamera();
    }
}
