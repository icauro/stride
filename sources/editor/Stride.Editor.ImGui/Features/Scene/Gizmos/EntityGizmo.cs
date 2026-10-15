using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;

namespace Stride.Assets.Presentation.AssetEditors.Gizmos;

// Engine ownership/scaling extracted from WPF EntityGizmo; the linked geometry
// classes retain their real SpriteComponent/ModelComponent rendering.
public abstract class EntityGizmo : GizmoBase
{
    public Entity ContentEntity { get; }
    public Entity? GizmoScalingEntity { get; protected set; }
    public virtual bool IsSelected { get; set; }
    public TransformGizmoHost Host { get; set; } = null!;
    public bool FixedSize { get; set; } = true;
    public float SceneUnit { get; set; } = 1;
    public float CameraAspectRatio { get; set; } = 1.7778f;
    protected override float DefaultCameraAspectRatio => CameraAspectRatio;
    protected EntityGizmo(Entity entity) => ContentEntity = entity;
    public virtual void Update()
    {
        ContentEntity.Transform.UpdateWorldMatrix();
        var position = ContentEntity.Transform.WorldMatrix.TranslationVector;
        var size = SizeFactor;
        if (FixedSize && !Host.IsOrthographic) size *= Math.Abs(Vector3.TransformCoordinate(position, Host.ViewMatrix).Z) / (SceneUnit * 10);
        ContentEntity.Transform.WorldMatrix.Decompose(out Vector3 _, out Quaternion rotation, out position);
        EditorScene.UpdateWorldMatrix();
        GizmoRootEntity.Transform.Position = position - EditorScene.WorldMatrix.TranslationVector;
        GizmoRootEntity.Transform.Rotation = rotation;
        (GizmoScalingEntity ?? GizmoRootEntity).Transform.Scale = new Vector3(size * SizeAdjustmentFactor * SceneUnit);
        GizmoRootEntity.Transform.UpdateWorldMatrix();
    }
}

public abstract class EntityGizmo<TComponent>(EntityComponent component) : EntityGizmo(component.Entity) where TComponent : EntityComponent
{
    protected TComponent Component { get; } = (TComponent)component;
}
