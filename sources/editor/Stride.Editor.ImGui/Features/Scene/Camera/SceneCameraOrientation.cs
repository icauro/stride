using Hexa.NET.ImGui;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.Font;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>The WPF orientation cube rendered into the embedded engine viewport.</summary>
internal sealed class SceneCameraOrientation : IDisposable
{
    private readonly Scene scene = new();
    private readonly CameraOrientationGizmo gizmo;
    private readonly OrientationHost host;
    private readonly EditorViewportCamera camera;
    private Int3? pressedElement;
    internal bool HasPointerSelection => gizmo.HasSelection;
    internal bool DisplayDirectionNames { get => host.DirectionNames; set => host.DirectionNames = value; }

    internal SceneCameraOrientation(Game game, Scene parent, GraphicsCompositor compositor, EditorViewportCamera camera, Stride.Core.IServiceRegistry? services = null)
    {
        this.camera = camera;
        host = new OrientationHost(game, compositor, camera);
        parent.Children.Add(scene);
        gizmo = new CameraOrientationGizmo(host);
        try
        {
            gizmo.Initialize(services ?? game.Services, scene);
            var sources = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Collect(Entity entity)
            {
                foreach (var component in entity.Components.OfType<ModelComponent>()) sources.Add(component);
                foreach (var child in entity.Transform.Children) Collect(child.Entity);
            }
            Collect(gizmo.GizmoRootEntity);
            // Native SpaceMarker uses the same stage name; this is the cube's newly added stage.
            var stage = compositor.RenderStages.Last(stage => stage.Name == "CameraOrientationGizmo");
            foreach (var mesh in compositor.RenderFeatures.OfType<MeshRenderFeature>())
                mesh.RenderStageSelectors.Add(new OrientationStageSelector(sources, stage));
            Update();
        }
        catch { Dispose(); throw; }
    }
    internal void Update()
    {
        scene.UpdateWorldMatrix();
        gizmo.GizmoRootEntity.Transform.Position = -scene.WorldMatrix.TranslationVector;
        gizmo.Update();
    }
    internal void CancelInteraction() { pressedElement = null; host.Pointer = new Vector2(-1, -1); }
    internal bool Handle(System.Numerics.Vector2 minimum, System.Numerics.Vector2 size, bool hovered, bool mouseAvailable)
    {
        if (!mouseAvailable || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        { CancelInteraction(); return false; }
        if (pressedElement.HasValue && Gui.IsKeyPressed(ImGuiKey.Escape))
        { CancelInteraction(); return true; }
        var io = Gui.GetIO();
        host.Pointer = hovered && size.X > 0 && size.Y > 0
            ? new Vector2((io.MousePos.X - minimum.X) / size.X, (io.MousePos.Y - minimum.Y) / size.Y)
            : new Vector2(-1, -1);
        Update();
        var captured = pressedElement.HasValue;
        var overCube = hovered && gizmo.HasSelection;
        if (overCube && !io.KeyAlt && Gui.IsMouseClicked(ImGuiMouseButton.Left))
        { pressedElement = gizmo.SelectedElement; captured = true; }
        if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (overCube && pressedElement is { } element && element == gizmo.SelectedElement)
            {
                // WPF toggles projection when an axis face is clicked in an axis-parallel view.
                if (gizmo.IsViewParallelToAxis && element.LengthSquared() == 1) camera.Orthographic = !camera.Orthographic;
                else camera.OrientDirection(new Vector3(-element.X, -element.Y, -element.Z));
            }
            pressedElement = null;
        }
        if (!Gui.IsMouseDown(ImGuiMouseButton.Left)) pressedElement = null;
        return captured || overCube && !io.KeyAlt;
    }
    private bool gizmoReleased;
    public void Dispose() { CancelInteraction(); if (!gizmoReleased) { gizmo.Dispose(); gizmoReleased = true; } scene.Parent?.Children.Remove(scene); }

    private sealed class OrientationHost(Game game, GraphicsCompositor compositor, EditorViewportCamera camera) : CameraOrientationGizmoHost
    {
        internal Vector2 Pointer = new(-1, -1);
        internal bool DirectionNames;
        public override Matrix ViewMatrix => camera.Component.ViewMatrix;
        public override Vector2 MousePosition => Pointer;
        public override bool DisplayDirectionNames => DirectionNames;
        public override GraphicsCompositor Compositor => compositor;
        public override IFontFactory FontFactory => game.Font;
    }
    private sealed class OrientationStageSelector(HashSet<object> sources, RenderStage stage) : RenderStageSelector
    {
        public override void Process(RenderObject renderObject)
        {
            if (!sources.Contains(renderObject.Source)) return;
            // The cube has its own camera/depth pass and must never become a scene entity pick.
            Array.Clear(renderObject.ActiveRenderStages);
            renderObject.ActiveRenderStages[stage.Index] = new ActiveRenderStage(CameraOrientationGizmoHost.EditorForwardShadingEffect);
        }
    }
}
