using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.Font;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

/// <summary>The original bottom-left axis marker in the real scene compositor.</summary>
internal sealed class SceneSpaceMarker : IDisposable
{
    private readonly Scene scene = new();
    private readonly SpaceMarker marker;
    private bool markerReleased;

    internal SceneSpaceMarker(Game game, Scene parent, GraphicsCompositor compositor, EditorViewportCamera camera, Stride.Core.IServiceRegistry? services = null)
    {
        marker = new SpaceMarker(new MarkerHost(game, compositor, camera));
        parent.Children.Add(scene);
        try
        {
            marker.Initialize(services ?? game.Services, scene);
            var components = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Collect(Entity entity)
            {
                foreach (var model in entity.Components.OfType<ModelComponent>()) components.Add(model);
                foreach (var child in entity.Transform.Children) Collect(child.Entity);
            }
            Collect(marker.GizmoRootEntity);
            // Use the actual stage instance: the marker/cube share the original stage name.
            var stage = marker.MarkerRenderStage;
            foreach (var feature in compositor.RenderFeatures.OfType<MeshRenderFeature>())
                feature.RenderStageSelectors.Add(new MarkerStageSelector(components, stage));
            Update();
        }
        catch { Dispose(); throw; }
    }

    internal void Update()
    {
        // HUD coordinates are independent of the authored root/child-scene offsets.
        scene.UpdateWorldMatrix();
        marker.GizmoRootEntity.Transform.Position = -scene.WorldMatrix.TranslationVector;
        marker.GizmoRootEntity.Transform.UpdateWorldMatrix();
        marker.Update();
    }

    public void Dispose()
    {
        if (!markerReleased) { marker.Dispose(); markerReleased = true; }
        scene.Parent?.Children.Remove(scene);
    }

    private sealed class MarkerHost(Game game, GraphicsCompositor compositor, EditorViewportCamera camera) : SpaceMarkerHost
    {
        public override Matrix ViewMatrix => camera.Component.ViewMatrix;
        public override GraphicsCompositor Compositor => compositor;
        public override IFontFactory FontFactory => game.Font;
    }

    private sealed class MarkerStageSelector(HashSet<object> sources, RenderStage stage) : RenderStageSelector
    {
        public override void Process(RenderObject renderObject)
        {
            if (!sources.Contains(renderObject.Source)) return;
            // These HUD meshes are not authored entities, scene picks or wireframe targets.
            Array.Clear(renderObject.ActiveRenderStages);
            renderObject.ActiveRenderStages[stage.Index] = new ActiveRenderStage(SpaceMarkerHost.EditorForwardShadingEffect);
        }
    }
}
