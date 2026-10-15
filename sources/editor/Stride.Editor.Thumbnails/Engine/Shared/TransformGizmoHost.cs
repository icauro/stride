using Stride.Engine.Processors;
using System.Threading.Tasks;
using Stride.Core.Mathematics;

namespace Stride.Engine.Gizmos;

/// <summary>Viewport input and camera supplied by an editor hosting transform gizmos.</summary>
public abstract class TransformGizmoHost
{
    public abstract CameraComponent Component { get; }
    public Matrix ViewMatrix => Component.ViewMatrix;
    public Matrix ProjectionMatrix => Component.ProjectionMatrix;
    public bool IsOrthographic => Component.Projection == CameraProjectionMode.Orthographic;
    public float AspectRatio => Component.AspectRatio;
    public float VerticalFieldOfView => Component.VerticalFieldOfView;
    public abstract Vector3 Position { get; }
    public abstract Vector2 MousePosition { get; }
    public abstract Vector2 ViewportSize { get; }
    public abstract bool IsMoving { get; }
    public abstract bool LeftButtonDown { get; }
    public abstract bool ControlDown { get; }
    public virtual bool UseLinearMovementForRotation => false;
    public virtual bool CanDuplicateSelection => false;
    public virtual Task DuplicateSelection() => Task.CompletedTask;
}



