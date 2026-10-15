using Stride.Core;
using Stride.Engine;
using Stride.Engine.Processors;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Assets.Presentation.AssetEditors.Gizmos;

/// <summary>Engine entity/resource ownership for the shared WPF transform gizmo sources.</summary>
public abstract class GizmoBase : ComponentBase
{
    public static readonly PropertyKey<bool> NoGizmoKey = new("NoGizmo", typeof(GizmoBase));
    protected Scene EditorScene { get; private set; } = null!;
    protected GraphicsDevice GraphicsDevice { get; private set; } = null!;
    protected Game Game { get; private set; } = null!;
    public Entity GizmoRootEntity { get; private set; } = null!;
    public RenderGroup RenderGroup { get; protected set; }
    public float SizeFactor { get; set; } = 1;
    protected virtual float SizeAdjustmentFactor => 1;
    public const RenderGroup PhysicsShapesGroup = RenderGroup.Group7;
    public const RenderGroup ParticlesShapesGroup = PhysicsShapesGroup;
    public const RenderGroup LightShaftsGroup = PhysicsShapesGroup;
    protected virtual bool OwnsMeshBuffers => true;
    public CommandList GraphicsCommandList { get; private set; } = null!;
    protected virtual float DefaultCameraAspectRatio => 1.7778f;
    public Stride.Engine.Gizmos.TransformGizmoHost? EditorCameraHost { get; set; }
    protected virtual Stride.Engine.Gizmos.TransformGizmoHost? GridCameraHost => EditorCameraHost;
    private bool enabled = true;
    public virtual bool IsEnabled
    {
        get => enabled;
        set
        {
            if (value == enabled) return;
            enabled = value;
            if (GizmoRootEntity == null) return;
            if (value) EditorScene.Entities.Add(GizmoRootEntity);
            else EditorScene.Entities.Remove(GizmoRootEntity);
        }
    }
    public virtual bool HandlesComponentId(OpaqueComponentId componentId, out Entity selection) { selection = null!; return false; }
    public virtual void Initialize(IServiceRegistry services, Scene editorScene)
    {
        EditorScene = editorScene;
        GraphicsDevice = services.GetService<IGraphicsDeviceService>().GraphicsDevice;
        Game = services.GetService<Stride.Games.IGame>() as Game ?? throw new InvalidOperationException("Gizmos require a running Stride game.");
        GraphicsCommandList = Game.GraphicsContext.CommandList;
        GizmoRootEntity = Create();
        if (GizmoRootEntity != null && enabled) EditorScene.Entities.Add(GizmoRootEntity);
    }
    protected abstract Entity Create();
    protected override void Destroy()
    {
        if (GizmoRootEntity != null)
        {
            EditorScene.Entities.Remove(GizmoRootEntity);
            var buffers = new HashSet<Stride.Graphics.Buffer>();
            void Collect(Entity entity)
            {
                foreach (var model in entity.Components.OfType<ModelComponent>().Where(component => component.Model != null))
                {
                    foreach (var mesh in model.Model.Meshes)
                    {
                        if (!OwnsMeshBuffers || mesh.Draw == null) continue;
                        foreach (var vertex in mesh.Draw.VertexBuffers) buffers.Add(vertex.Buffer);
                        if (mesh.Draw.IndexBuffer != null) buffers.Add(mesh.Draw.IndexBuffer.Buffer);
                    }
                }
                foreach (var child in entity.Transform.Children) Collect(child.Entity);
            }
            if (OwnsMeshBuffers) Collect(GizmoRootEntity);
            foreach (var buffer in buffers) buffer.Dispose();
        }
        base.Destroy();
    }
}
