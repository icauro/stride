using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed class SceneViewportGrid : IDisposable
{
    private readonly Scene scene = new();
    private readonly ViewportGridGizmo grid;
    private readonly EditorViewportCamera camera;
    public SceneGridSettings Settings { get; private set; } = SceneGridSettings.Default;

    internal SceneViewportGrid(Game game, Scene parent, GraphicsCompositor compositor, EditorViewportCamera camera, Stride.Core.IServiceRegistry? services = null)
    {
        this.camera = camera;
        parent.Children.Add(scene);
        grid = new ViewportGridGizmo { EditorCameraHost = new GridCameraHost(camera) };
        try { grid.Initialize(services ?? game.Services, scene); }
        catch { grid.Dispose(); scene.Parent?.Children.Remove(scene); throw; }
        var components = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Collect(Entity entity)
        {
            foreach (var model in entity.Components.OfType<ModelComponent>()) components.Add(model);
            foreach (var child in entity.Transform.Children) Collect(child.Entity);
        }
        Collect(grid.GizmoRootEntity);
        var stage = new RenderStage("GridGizmo", "Main") { SortMode = new BackToFrontSortMode() };
        compositor.RenderStages.Add(stage);
        foreach (var mesh in compositor.RenderFeatures.OfType<MeshRenderFeature>())
        {
            mesh.RenderStageSelectors.Add(new GridStageSelector(components, stage));
            mesh.PipelineProcessors.Add(new AlphaBlendPipelineProcessor { RenderStage = stage });
        }
        ((EditorTopLevelCompositor)compositor.Game).PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = stage });
        Update();
    }
    internal void ApplySettings(SceneGridSettings settings)
    {
        if (!settings.Valid) throw new ArgumentException("Invalid grid settings.", nameof(settings));
        Settings = settings;
    }
    internal void Update()
    {
        grid.IsEnabled = Settings.Visible;
        if (!Settings.Visible) return;
        // Grid coordinates are world coordinates, independent of the authored scene offset.
        scene.UpdateWorldMatrix(); grid.GizmoRootEntity.Transform.Position = -scene.WorldMatrix.TranslationVector;
        grid.Update(new Color3(Settings.Color.X, Settings.Color.Y, Settings.Color.Z), Settings.Opacity, Settings.Axes, camera.SceneUnit);
    }
    private bool gridReleased;
    public void Dispose() { if (!gridReleased) { grid.Dispose(); gridReleased = true; } scene.Parent?.Children.Remove(scene); }
    private sealed class GridStageSelector(HashSet<object> models, RenderStage stage) : RenderStageSelector
    {
        public override void Process(RenderObject renderObject)
        {
            if (!models.Contains(renderObject.Source)) return;
            Array.Clear(renderObject.ActiveRenderStages);
            renderObject.ActiveRenderStages[stage.Index] = new ActiveRenderStage("StrideEditorForwardShadingEffect");
        }
    }
    private sealed class GridCameraHost(EditorViewportCamera camera) : TransformGizmoHost
    {
        public override CameraComponent Component => camera.Component;
        public override Vector3 Position => camera.Position;
        public override Vector2 MousePosition => Vector2.Zero;
        public override Vector2 ViewportSize => Vector2.One;
        public override bool IsMoving => false;
        public override bool LeftButtonDown => false;
        public override bool ControlDown => false;
    }
}
