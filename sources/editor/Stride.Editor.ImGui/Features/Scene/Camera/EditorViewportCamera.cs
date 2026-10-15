using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Processors;

namespace Stride.Editor.ImGui;

internal enum EditorCameraOrientation { Front, Back, Top, Bottom, Left, Right }

/// <summary>Input-neutral adaptation of the WPF entity editor's perspective camera.</summary>
internal sealed class EditorViewportCamera
{
    public CameraComponent Component { get; } = new() { UseCustomViewMatrix = true };
    public Vector3 Position { get; private set; }
    public Vector3 Target { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    private float moveSpeed = 3;
    public bool InvertPanningAxis { get; set; } = true;
    public float MouseMoveSpeedFactor { get; set; } = 100;
    public float MoveSpeed { get => moveSpeed; set { if (float.IsFinite(value)) moveSpeed = Math.Clamp(value, 0.01f, 10000); } }
    public int MoveSpeedIndex { get => Stride.Engine.Design.EditorCameraMovement.FindIndex(MoveSpeed); set => MoveSpeed = Stride.Engine.Design.EditorCameraMovement.ValueAt(value); }
    public void StepMovementSpeed(int direction) => MoveSpeedIndex += Math.Sign(direction);
    private float sceneUnit = 1;
    public float SceneUnit { get => sceneUnit; set { if (float.IsFinite(value) && value > MathUtil.ZeroTolerance) sceneUnit = value; } }
    public float Radius { get; private set; }
    public EditorCameraOrientation? Orientation { get; private set; }
    public static EditorCameraOrientation[] Orientations { get; } = Enum.GetValues<EditorCameraOrientation>();
    internal static Vector3 Direction(EditorCameraOrientation orientation) => orientation switch
    {
        EditorCameraOrientation.Front => Vector3.UnitZ, EditorCameraOrientation.Back => -Vector3.UnitZ,
        EditorCameraOrientation.Top => -Vector3.UnitY, EditorCameraOrientation.Bottom => Vector3.UnitY,
        EditorCameraOrientation.Left => Vector3.UnitX, EditorCameraOrientation.Right => -Vector3.UnitX,
        _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
    };
    public void Orient(EditorCameraOrientation orientation) => OrientDirection(Direction(orientation));
    internal void OrientDirection(Vector3 direction)
    {
        if (direction.LengthSquared() <= MathUtil.ZeroTolerance) return;
        direction.Normalize();
        Yaw = direction.X == 0 && direction.Z == 0 ? 0 : MathF.Atan2(-direction.X, -direction.Z);
        Pitch = MathF.Atan2(direction.Y, new Vector2(direction.X, direction.Z).Length());
        // CameraOrientationService.ResetCamera changes heading at the current camera position.
        Target = Position + Forward * Radius;
        Orientation = null;
        foreach (var orientation in Orientations)
            if (Vector3.DistanceSquared(Direction(orientation), direction) < 1e-6f) { Orientation = orientation; break; }
        UpdateMatrix();
    }
    public bool Orthographic
    {
        get => Component.Projection == CameraProjectionMode.Orthographic;
        set => Component.Projection = value ? CameraProjectionMode.Orthographic : CameraProjectionMode.Perspective;
    }
    public float OrthographicSize
    {
        get => Component.OrthographicSize;
        set { if (!float.IsFinite(value) || value <= 0) throw new ArgumentException("Orthographic size must be finite and greater than zero."); Component.OrthographicSize = value; }
    }
    public EditorViewportCamera() => Reset();
    public void SetLens(float fov, float near, float far)
    {
        if (!CameraLens.Valid(fov, near, far)) throw new ArgumentException("Use a finite field of view between 0 and 180 degrees and 0 < Near < Far.");
        Component.VerticalFieldOfView = fov; Component.NearClipPlane = near; Component.FarClipPlane = far;
    }
    internal SceneCameraSettings Capture(bool selection) => new(2, CameraVector.From(Position), CameraVector.From(Target),
        Yaw, Pitch, Radius, MoveSpeed, Orthographic, OrthographicSize, Orientation, selection,
        Component.VerticalFieldOfView, Component.NearClipPlane, Component.FarClipPlane, SceneUnit: SceneUnit);
    internal void Restore(SceneCameraSettings settings)
    {
        if (!settings.Valid) throw new ArgumentException("Invalid camera preferences.");
        Position = settings.Position.Vector; Target = settings.Target.Vector; Yaw = settings.Yaw; Pitch = settings.Pitch;
        Radius = settings.Radius; MoveSpeed = settings.Speed; Orthographic = settings.Orthographic;
        OrthographicSize = settings.Size; Orientation = settings.Orientation; UpdateMatrix();
        SetLens(settings.Fov, settings.Near, settings.Far);
        SceneUnit = settings.SceneUnit;
    }
    public void Reset()
    {
        Orientation = null;
        Position = new Vector3(4, 2, 4); Yaw = MathUtil.PiOverFour; Pitch = -MathUtil.Pi / 12;
        Target = Vector3.Zero; Radius = Position.Length(); UpdateMatrix();
    }
    private Vector3 Forward => Vector3.Transform(new Vector3(0, 0, -1), Quaternion.RotationYawPitchRoll(Yaw, Pitch, 0));
    public void Focus(Vector3 center, float radius)
    {
        Target = center; Radius = 3 * (Math.Abs(radius) > MathUtil.ZeroTolerance ? radius : 0.5f * SceneUnit);
        Position = Target - Forward * Radius; UpdateMatrix();
    }
    internal void UpdateOrthographicSceneDistance(float sceneRadius)
    {
        if (!Orthographic || !float.IsFinite(sceneRadius) || sceneRadius < 0) return;
        Radius = Math.Max(10f, sceneRadius * 2f);
        Position = Target - Forward * Radius; UpdateMatrix();
    }
    public void Navigate(Vector2 delta, float wheel, bool left, bool middle, bool right, bool alt, bool shift,
        Vector3 movement, float dt, float? sceneRadius = null)
    {
        var pan = middle && !right;
        var look = !alt && !middle && right;
        var move = !alt && middle && right;
        var orbit = alt && left && !middle && !right;
        var zoom = (alt && !left && !middle && right) || Math.Abs(wheel) > MathUtil.ZeroTolerance;
        var speed = Math.Clamp(MoveSpeed, 0.01f, 10000) * SceneUnit * (shift ? 10 : 1);
        if (look || move || orbit)
        {
            if (delta.X != 0 || (look || orbit) && delta.Y != 0) Orientation = null;
            var rotationSpeed = 0.75f * MathUtil.Pi * (orbit ? 2 : 1);
            Yaw -= 1.333f * delta.X * rotationSpeed;
            if (look || orbit) Pitch = Math.Clamp(Pitch - delta.Y * rotationSpeed, -MathUtil.PiOverTwo, MathUtil.PiOverTwo);
        }
        var rotation = Quaternion.RotationYawPitchRoll(Yaw, Pitch, 0);
        var position = Position;
        if (Orthographic && sceneRadius is { } actualRadius && float.IsFinite(actualRadius) && actualRadius >= 0)
        {
            // Native orthographic navigation keeps the camera outside the scene
            // bounding volume, using the heading calculated for this frame.
            Radius = Math.Max(10f, actualRadius * 2f);
            position = Target - Forward * Radius;
        }
        var zoomDelta = 0f;
        if (pan || move || look)
        {
            if (Orthographic)
            {
                // Native orthographic WASD stays in the camera plane; the
                // configured up/down pair controls zoom instead of depth.
                zoomDelta += movement.Y;
                movement = new Vector3(movement.X, -movement.Z, 0);
            }
            if (movement.LengthSquared() > 0)
                position += Vector3.Transform(Vector3.Normalize(movement), rotation) * speed * Math.Clamp(dt, 0, 1);
        }
        if (pan) position += Vector3.Transform(new Vector3(delta.X, -delta.Y, 0), rotation) * (Orthographic ? OrthographicSize : Radius) * (InvertPanningAxis ? -1 : 1) * MouseMoveSpeedFactor * (speed / 60) * 0.033f;
        if (move)
        {
            var forward = Forward;
            var projected = new Vector3(forward.X, 0, forward.Z);
            if (Orthographic) zoomDelta -= MouseMoveSpeedFactor * delta.Y;
            else if (projected.LengthSquared() > 0) position -= Vector3.Normalize(projected) * (speed / 60) * MouseMoveSpeedFactor * delta.Y;
        }
        if (look && wheel != 0) StepMovementSpeed(Math.Sign(wheel));
        else if (zoom)
        {
            if (Orthographic) zoomDelta += 12 * wheel + (left || middle || right ? MouseMoveSpeedFactor * (delta.X + delta.Y) : 0);
            else
            {
                position += Forward * (12 * wheel * 0.1f + (left || middle || right ? (speed / 60) * MouseMoveSpeedFactor * (delta.X + delta.Y) : 0));
                Radius = Math.Max(0.001f, Vector3.Distance(Target, position));
            }
        }
        if (Orthographic)
        {
            var nextSize = Component.OrthographicSize - (speed / 60) * zoomDelta;
            if (nextSize > 0 && float.IsFinite(nextSize)) Component.OrthographicSize = nextSize;
        }
        else
        {
            // Original UpdateCameraBase applies this navigation policy even
            // when this frame does not produce a field-of-view zoom delta.
            Component.VerticalFieldOfView = Math.Clamp(Component.VerticalFieldOfView - (speed / 60) * zoomDelta, 20, 120);
        }
        if (orbit) position = Target - Forward * Radius;
        else Target = position + Forward * Radius;
        Position = position; UpdateMatrix();
    }
    private void UpdateMatrix() => Component.ViewMatrix = Matrix.Translation(-Position)
        * Matrix.RotationQuaternion(Quaternion.Invert(Quaternion.RotationYawPitchRoll(Yaw, Pitch, 0)));
}
