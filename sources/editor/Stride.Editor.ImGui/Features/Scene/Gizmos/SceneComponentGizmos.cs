using Stride.Physics.Editor;
using Stride.Particles.Editor;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Engine.Gizmos;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Sprites;
using Stride.Physics;

namespace Stride.Editor.ImGui;

internal sealed partial class SceneComponentGizmos : IDisposable
{
    private sealed record Entry(EntityGizmo Gizmo, Type GeometryType)
    {
        internal readonly HashSet<EntityComponent> Registered = [];
        internal readonly HashSet<EntityComponent> Current = [];
        internal readonly List<EntityComponent> Retired = [];
    }
    private readonly Game game;
    private readonly Stride.Core.IServiceRegistry services;
    private readonly Scene scene = new();
    private readonly EditorPickingRenderer picker;
    private readonly ComponentCameraHost host;
    private float cameraAspect;
    internal float CameraAspectRatio { get => cameraAspect; set => cameraAspect = value; }
    private readonly Dictionary<EntityComponent, Entry> entries = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<EntityComponent> wanted = new(ReferenceEqualityComparer.Instance);
    private readonly List<EntityComponent> removing = [];
    private readonly HashSet<object> renderComponents = new(ReferenceEqualityComparer.Instance);
    private readonly SceneComponentGizmoRegistry registry = new();
    private readonly HashSet<string> hidden = [typeof(ModelComponent).FullName!];
    internal IReadOnlyList<SceneComponentGizmoRegistry.Family> Families => registry.Families;
    internal void RefreshRegistry(long providerVersion) { registry.Refresh(providerVersion); ResolveNativeHiddenFamilies(); }
    internal bool IsVisible(SceneComponentGizmoRegistry.Family family) => !hidden.Contains(family.Id);
    internal void SetVisible(SceneComponentGizmoRegistry.Family family, bool visible) { if (visible) hidden.Remove(family.Id); else hidden.Add(family.Id); }
    private bool Visible(Type component) => !hidden.Contains(component.FullName!);
    private void Visible(Type component, bool visible) { if (visible) hidden.Remove(component.FullName!); else hidden.Add(component.FullName!); }
    public bool CameraVisible { get => Visible(typeof(CameraComponent)); set => Visible(typeof(CameraComponent), value); }
    public bool LightVisible { get => Visible(typeof(LightComponent)); set => Visible(typeof(LightComponent), value); }
    public bool AudioEmitterVisible { get => Visible(typeof(AudioEmitterComponent)); set => Visible(typeof(AudioEmitterComponent), value); }
    public bool AudioListenerVisible { get => Visible(typeof(AudioListenerComponent)); set => Visible(typeof(AudioListenerComponent), value); }
    private float size = 1;
    private bool fixedSize;
    internal SceneComponentGizmoSettings CaptureSettings() => new(CameraVisible, LightVisible, AudioEmitterVisible, AudioListenerVisible, size, fixedSize, hidden.Order(StringComparer.Ordinal).ToArray());
    internal void RestoreSettings(SceneComponentGizmoSettings settings)
    {
        if (!settings.Valid) throw new ArgumentException("Invalid component gizmo preferences.");
        pendingNativeHiddenNames.Clear(); hidden.Clear(); foreach (var type in settings.HiddenTypes ?? [typeof(ModelComponent).FullName!]) hidden.Add(type);
        CameraVisible = settings.Camera; LightVisible = settings.Light; AudioEmitterVisible = settings.AudioEmitter; AudioListenerVisible = settings.AudioListener;
        size = settings.Size; fixedSize = settings.FixedSize;
    }
    public int Count => entries.Count;
    internal bool HasSelectedLightProbes { get; private set; }

    internal SceneComponentGizmos(Game game, Scene parent, GraphicsCompositor compositor, EditorViewportCamera camera,
        RenderStage pickingStage, EditorPickingRenderer picker, float cameraAspect, Stride.Core.IServiceRegistry? services = null)
    {
        this.game = game; this.services = services ?? game.Services; this.picker = picker; this.cameraAspect = cameraAspect; host = new(camera);
        Stride.Physics.Simulation.DisableSimulation = true;
        parent.Children.Add(scene);
        var opaque = new RenderStage("Component gizmos", "Main");
        var transparent = new RenderStage("Component gizmo billboards", "Main") { SortMode = new BackToFrontSortMode() };
        var wireframe = new RenderStage("Component wireframe gizmos", "Main");
        var probeSelection = new RenderStage("LightProbe Selection Gizmo", "Wireframe") { Filter = new SelectedProbeFilter() };
        compositor.RenderStages.Add(opaque); compositor.RenderStages.Add(transparent); compositor.RenderStages.Add(wireframe); compositor.RenderStages.Add(probeSelection);
        foreach (var mesh in compositor.RenderFeatures.OfType<MeshRenderFeature>())
        {
            mesh.RenderStageSelectors.Add(new ComponentGizmoStageSelector(renderComponents, opaque, transparent, wireframe, probeSelection, pickingStage));
            mesh.PipelineProcessors.Add(new MeshPipelineProcessor { TransparentRenderStage = transparent });
            mesh.PipelineProcessors.Add(new WireframePipelineProcessor { RenderStage = wireframe });
        }
        var sprites = compositor.RenderFeatures.OfType<SpriteRenderFeature>().FirstOrDefault();
        if (sprites == null) { sprites = new SpriteRenderFeature(); compositor.RenderFeatures.Add(sprites); }
        sprites.RenderStageSelectors.Add(new ComponentGizmoStageSelector(renderComponents, opaque, transparent, wireframe, probeSelection, pickingStage));
        var top = (EditorTopLevelCompositor)compositor.Game;
        top.PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = opaque });
        top.PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = transparent });
        top.PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = wireframe });
        top.PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = probeSelection });
    }

    internal void Synchronize(EntityHierarchyDocument document, IEnumerable<Entity> entities,
        Func<Entity, EntityHierarchyDocument?>? resolveOwner = null, Func<Entity, bool>? canSelect = null,
        Func<Entity, bool>? isWorldSelected = null)
    {
        if (scene.Parent is { } parent)
        {
            parent.UpdateWorldMatrix();
            scene.Offset = -parent.WorldMatrix.TranslationVector;
            scene.UpdateWorldMatrix();
        }
        wanted.Clear();
        registry.Refresh();
        HasSelectedLightProbes = false;
        foreach (var entity in entities)
        {
            if (entity.Tags.Get(GizmoBase.NoGizmoKey)) continue;
            EntityComponent? main = null; var order = int.MinValue; var created = false;
            foreach (var component in entity.Components)
            {
                if (component is TransformComponent) continue;
                var family = registry.Resolve(component.GetType());
                if (family == null) continue;
                if (!family.Main) { SynchronizeComponent(component, family, entity); created = true; continue; }
                if (!IsVisible(family) || GeometryType(component) == null) continue;
                var componentOrder = registry.Order(component.GetType());
                if (componentOrder > order) { main = component; order = componentOrder; }
            }
            if (main != null) { SynchronizeComponent(main, registry.Resolve(main.GetType())!, entity); created = true; }
            if (!created) SynchronizeComponent(entity.Transform, registry.Resolve(typeof(TransformComponent))!, entity);
        }
        removing.Clear();
        foreach (var component in entries.Keys) if (!wanted.Contains(component)) removing.Add(component);
        foreach (var component in removing) Remove(component, entries[component]);
        registry.ReleaseUnusedGenerations(entries.Keys);

        void SynchronizeComponent(EntityComponent component, SceneComponentGizmoRegistry.Family family, Entity entity)
        {
            var type = GeometryType(component); if (type == null) return;
            wanted.Add(component);
            if (entries.TryGetValue(component, out var entry) && entry.GeometryType != type) { Remove(component, entry); entry = null; }
            if (entry == null)
            {
                var gizmo = Create(component, type);
                try
                {
                    gizmo.Host = host; gizmo.CameraAspectRatio = cameraAspect;
                    gizmo.Initialize(services, scene);
                    // Collect selected-only geometry too, including detachable frusta/range meshes.
                    gizmo.IsSelected = true;
                    gizmo.IsSelected = false;
                    entries.Add(component, entry = new(gizmo, type));
                }
                catch { gizmo.Dispose(); throw; }
            }
            var owner = resolveOwner?.Invoke(entity) ?? document;
            // Native CameraGizmo compares live default aspect alongside its real camera fields.
            entry.Gizmo.CameraAspectRatio = cameraAspect;
            entry.Gizmo.IsEnabled = IsVisible(family);
            entry.Gizmo.SizeFactor = size; entry.Gizmo.FixedSize = fixedSize;
            entry.Gizmo.IsSelected = isWorldSelected != null ? isWorldSelected(entity)
                : owner.IsSelected(entity.Id) && canSelect?.Invoke(entity) != false;
            if (entry.Gizmo is LightProbeGizmo && entry.Gizmo.IsEnabled && entry.Gizmo.IsSelected) HasSelectedLightProbes = true;
            entry.Gizmo.Update();
            RegisterGeometry(entry, entity);
        }
    }

    private Type? GeometryType(EntityComponent component) => component switch
    {
        LightComponent { Type: LightDirectional } => typeof(LightDirectionalGizmo),
        LightComponent { Type: LightPoint } => typeof(LightPointGizmo),
        LightComponent { Type: LightSpot } => typeof(LightSpotGizmo),
        LightComponent { Type: LightAmbient } => typeof(AmbientLightGizmo),
        LightComponent { Type: LightSkybox } => typeof(SkyboxLightGizmo),
        LightComponent => null,
        _ => registry.Resolve(component.GetType())?.GeometryType,
    };
    private static EntityGizmo Create(EntityComponent component, Type type) => type == typeof(AmbientLightGizmo) ? new AmbientLightGizmo(component)
        : type == typeof(SkyboxLightGizmo) ? new SkyboxLightGizmo(component)
        : type == typeof(LightShaftBoundingVolumeGizmo) ? new BorrowedLightShaftGizmo(component)
        : type == typeof(LightProbeGizmo) ? new SharedLightProbeGizmo(component)
        : type == typeof(PhysicsConstraintGizmo) ? new SharedConstraintGizmo((PhysicsConstraintComponent)component)
        : type == typeof(PhysicsGizmo) ? new SharedPhysicsGizmo((PhysicsComponent)component)
        : (EntityGizmo)Activator.CreateInstance(type, component)!;
    private void RegisterGeometry(Entry entry, Entity owner)
    {
        entry.Current.Clear();
        void Collect(Entity? root)
        {
            if (root == null) return;
            foreach (var component in root.Components) entry.Current.Add(component);
            foreach (var child in root.Transform.Children) Collect(child.Entity);
        }
        Collect(entry.Gizmo.GizmoRootEntity);
        if (entry.Gizmo is ParticleSystemGizmo particles) Collect(particles.DebugShapeEntity);
        if (entry.Gizmo is PhysicsGizmo physics) foreach (var root in physics.DebugEntities) Collect(root);
        entry.Retired.Clear();
        foreach (var component in entry.Registered) if (!entry.Current.Contains(component)) entry.Retired.Add(component);
        if (entry.Retired.Count != 0) picker.Cancel("Component gizmo geometry changed");
        foreach (var component in entry.Retired)
        { picker.ForgetGizmoComponent(component); renderComponents.Remove(component); entry.Registered.Remove(component); }
        foreach (var component in entry.Current)
            if (entry.Registered.Add(component))
            {
                picker.CacheGizmoComponent(component, owner);
                if (component is ModelComponent or SpriteComponent) renderComponents.Add(component);
            }
    }
    private void Remove(EntityComponent component, Entry entry)
    {
        picker.Cancel("Component gizmo changed");
        entry.Gizmo.IsSelected = true;
        foreach (var registered in entry.Registered) { picker.ForgetGizmoComponent(registered); renderComponents.Remove(registered); }
        entry.Gizmo.Dispose(); entries.Remove(component);
        registry.RequestPrune();
    }
    public void Dispose()
    {
        foreach (var item in entries.ToArray()) Remove(item.Key, item.Value);
        registry.Dispose();
        scene.Parent?.Children.Remove(scene); renderComponents.Clear();
    }

    private sealed class AmbientLightGizmo(EntityComponent component) : LightGizmo(component, "Ambient", GizmoResources.AmbientLightGizmo);
    private sealed class SkyboxLightGizmo(EntityComponent component) : LightGizmo(component, "SkyboxLight", GizmoResources.SkyboxLightGizmo);
    private sealed class BorrowedLightShaftGizmo(EntityComponent component) : LightShaftBoundingVolumeGizmo(component)
    { protected override bool OwnsMeshBuffers => false; }
    private sealed class SharedLightProbeGizmo(EntityComponent component) : LightProbeGizmo(component)
    { protected override bool OwnsMeshBuffers => false; }
    private sealed class SharedConstraintGizmo(PhysicsConstraintComponent component) : PhysicsConstraintGizmo(component)
    { protected override bool OwnsMeshBuffers => false; }
    private sealed class SharedPhysicsGizmo(PhysicsComponent component) : PhysicsGizmo(component)
    { protected override bool OwnsMeshBuffers => false; }
    private sealed class ComponentCameraHost(EditorViewportCamera camera) : TransformGizmoHost
    {
        public override CameraComponent Component => camera.Component;
        public override Vector3 Position => camera.Position;
        public override Vector2 MousePosition => Vector2.Zero;
        public override Vector2 ViewportSize => Vector2.One;
        public override bool IsMoving => false;
        public override bool LeftButtonDown => false;
        public override bool ControlDown => false;
    }
    private sealed class SelectedProbeFilter : RenderStageFilter
    {
        public override bool IsVisible(RenderObject renderObject, RenderView renderView, RenderViewStage renderViewStage) =>
            (renderObject.Source as ModelComponent)?.Entity?.Tags.Get(GizmoSelectionKeys.Selected) ?? false;
    }
    private sealed class ComponentGizmoStageSelector(HashSet<object> components, RenderStage opaque, RenderStage transparent, RenderStage wireframe, RenderStage probeSelection, RenderStage picking) : RenderStageSelector
    {
        public override void Process(RenderObject renderObject)
        {
            if (!components.Contains(renderObject.Source)) return;
            Array.Clear(renderObject.ActiveRenderStages);
            renderObject.ActiveRenderStages[picking.Index] = new ActiveRenderStage(renderObject is RenderSprite ? "SpritePicking" : "StrideEditorForwardShadingEffect.Picking");
            var stage = renderObject.RenderGroup == GizmoBase.PhysicsShapesGroup ? wireframe
                : renderObject is RenderSprite || renderObject is RenderMesh { MaterialPass.HasTransparency: true } ? transparent : opaque;
            renderObject.ActiveRenderStages[stage.Index] = new ActiveRenderStage("StrideEditorForwardShadingEffect");
            if (renderObject.RenderGroup == LightProbeGizmo.LightProbeGroup)
                renderObject.ActiveRenderStages[probeSelection.Index] = new ActiveRenderStage("StrideEditorForwardShadingEffect.Wireframe");
        }
    }
}
