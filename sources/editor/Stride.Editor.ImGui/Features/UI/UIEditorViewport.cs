using Stride.Assets.Rendering;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.UI.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Engine.Processors;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.UI;
using Stride.UI;
using Stride.UI.Controls;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Isolated production Stride UI layout/rendering over an authored hierarchy clone and real compiled resources.</summary>
internal sealed partial class UIEditorViewport : IDisposable, IRuntimeTextureConsumer
{
    private readonly Game game;
    private readonly ImGuiRenderer renderer;
    private readonly ContentManager content;
    private readonly IDisposable? contentLease;
    private readonly Func<Type, string, UIEditorResourceLease?>? resolveResource;
    private readonly Dictionary<(Type Type, string Url), UIEditorResourceLease> borrowedResources = [];
    private readonly Dictionary<(Type Type, string Url), object> resources = [];
    private readonly PreviewSceneSystem sceneSystem;
    private readonly Entity cameraEntity;
    private readonly CameraComponent camera;
    private readonly UIComponent component;
    private readonly Entity uiEntity;
    private readonly Dictionary<Guid, UIElement> runtime = [];
    private readonly ColorSpace colorSpace;
    private Texture? target, depth, presentation;
    private UIDocument? document;
    private Guid? rootId;
    private long observedVersion = -1;
    private bool disposed;
    public bool UsesRuntimeTexture(Texture texture) => !disposed && (CompiledTextureConsumers.UsesTexture(component, texture)
        || CompiledTextureConsumers.UsesTexture(sceneSystem?.SceneInstance?.RootScene, texture)
        || CompiledTextureConsumers.UsesTexture(content, texture));
    public bool Visible { get; set; }
    public ImTextureRef TextureRef { get; private set; }
    public Exception? RenderError { get; private set; }
    public float Zoom { get; private set; } = 1;
    public NVector2 Pan { get; private set; }

    public UIEditorViewport(Game game, ImGuiRenderer renderer, CompiledProjectContent project, ColorSpace colorSpace, IDisposable? contentLease = null, Func<Type, string, UIEditorResourceLease?>? resolveResource = null, PackageTypeScope? scope = null)
    {
        this.game = game; this.renderer = renderer; this.colorSpace = colorSpace; this.contentLease = contentLease; this.resolveResource = resolveResource; content = project.CreateObjectCache();
        camera = new CameraComponent { Projection = CameraProjectionMode.Orthographic, NearClipPlane = .1f, FarClipPlane = 1000 };
        cameraEntity = new Entity("UI editor camera") { camera }; cameraEntity.Transform.Position = cameraPosition;
        component = new UIComponent { IsFullScreen = false, IsBillboard = false, ResolutionStretch = ResolutionStretch.FixedWidthFixedHeight,
            Page = new UIPage() };
        uiEntity = new Entity("UI editor authored layout") { component };
        var scene = new Scene(); scene.Entities.Add(cameraEntity);
        var designArea = new Entity("UI editor design area")
        {
            new UIComponent { IsFullScreen = false, IsBillboard = false, ResolutionStretch = ResolutionStretch.FixedWidthFixedHeight,
                Page = new UIPage { RootElement = new Border { BackgroundColor = Color.WhiteSmoke * .5f, BorderColor = Color.WhiteSmoke, BorderThickness = Thickness.UniformCuboid(2) } } }
        };
        designArea.Transform.Position.Z = -10;
        // Match the native UI editor: submit the design background before authored UI.
        scene.Entities.Add(designArea); scene.Entities.Add(uiEntity);
        PreviewSceneSystem? created = null;
        try
        {
            created = new PreviewSceneSystem(game);
            created.GraphicsCompositor.Dispose();
            created.GraphicsCompositor = GraphicsCompositorHelper.CreateDefault(false, "StrideEditorForwardShadingEffect", camera,
                new Color(51, 51, 51, 255));
            var opaqueStage = created.GraphicsCompositor.RenderStages.First(stage => stage.Name == "Opaque");
            var transparentStage = created.GraphicsCompositor.RenderStages.First(stage => stage.Name == "Transparent");
            EditorPackageRenderFeatures.Add(created.GraphicsCompositor, opaqueStage, transparentStage, scope);
            created.SceneInstance = new SceneInstance(game.Services, scene, ExecutionMode.Preview);
            created.InitializePreview(); sceneSystem = created;
            DesignArea = designArea.Get<UIComponent>();
        }
        catch { created?.Dispose(); throw; }
    }
    private UIComponent DesignArea { get; }
    public void Synchronize(UIDocument source, Guid? displayedRoot)
    {
        if (ReferenceEquals(document, source) && rootId == displayedRoot && observedVersion == source.LayoutVersion) return;
        // UI hierarchy parts have reference-serialized child links. Clone their owning
        // asset so those links resolve to the cloned Parts instead of external proxies.
        var assetClone = (UIAssetBase)AssetCloner.Clone(source.Asset);
        var nextId = displayedRoot ?? source.Roots.FirstOrDefault()?.Id;
        UIElement? cloned = nextId is { } id && assetClone.Hierarchy.Parts.TryGetValue(id, out var design)
            ? design.UIElement : null;
        if (cloned != null)
        {
            var collector = new UIResourceCollector(); collector.Visit(cloned);
            foreach (var reference in collector.References)
            {
                var key = (reference.Type, reference.Url);
                if (!resources.TryGetValue(key, out var loaded))
                {
                    var borrowed = resolveResource?.Invoke(reference.Type, reference.Url);
                    if (borrowed != null) { loaded = borrowed.Value; borrowedResources.Add(key, borrowed); }
                    else loaded = content.Load(reference.Type, reference.Url, ContentManagerLoaderSettings.StreamingDisabled);
                    resources.Add(key, loaded);
                }
                if (!reference.Path.Apply(cloned, MemberPathAction.ValueSet, loaded)) throw new InvalidOperationException("A compiled UI resource cannot be assigned to the runtime hierarchy.");
            }
        }
        document = source; rootId = displayedRoot; observedVersion = source.LayoutVersion;
        component.Page = new UIPage { RootElement = cloned };
        runtime.Clear(); if (cloned != null) foreach (var element in UIDocument.Enumerate(cloned)) runtime[element.Id] = element;
        component.Resolution = DesignArea.Resolution = source.Asset.Design.Resolution;
        component.Size = DesignArea.Size = source.Asset.Design.Resolution / 100;
        UpdateCamera(); RenderError = null;
    }
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        width = Math.Clamp(width, 1, 4096); height = Math.Clamp(height, 1, 4096);
        if (target?.Width == width && target.Height == height) return;
        var format = colorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var hostFormat = game.GraphicsDevice.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var next = new PreviewTargetResources(game, renderer);
        var orthographicSize = camera.OrthographicSize;
        try
        {
            next.Allocate(width, height, format, hostFormat, depth: true);
            ReleaseTarget();
        }
        catch { next.Retire(); throw; }
        targetResources = next; target = next.Target; depth = next.Depth; presentation = next.Presentation; TextureRef = next.Reference;
        SetOrthographicSize(orthographicSize); RenderError = null;
    }
    public void ResetZoom() => SetOrthographicSize(CameraComponent.DefaultOrthographicSize);
    public void StepZoom(float amount) => SetOrthographicSize(camera.OrthographicSize + amount);
    public void SetOrthographicSize(float size)
    {
        if (!float.IsFinite(size) || size <= 0) return;
        camera.OrthographicSize = size;
        UpdateCamera();
    }
    private void UpdateCamera()
    {
        if (target != null) Zoom = target.Height / (camera.OrthographicSize * 100);
        Pan = new NVector2(-cameraPosition.X, cameraPosition.Y) * (Zoom * 100);
        cameraEntity.Transform.Position = cameraPosition;
        cameraEntity.Transform.Rotation = Quaternion.RotationYawPitchRoll(cameraPitchYaw.Y, cameraPitchYaw.X, 0);
    }
    public bool TryElementRect(Guid id, out NVector2 minimum, out NVector2 maximum)
    {
        minimum = maximum = NVector2.Zero;
        if (target == null || !runtime.TryGetValue(id, out var element) || element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var matrix = element.WorldMatrix * RenderWorldViewProjection();
        var half = element.RenderSize / 2;
        minimum = new NVector2(float.PositiveInfinity); maximum = new NVector2(float.NegativeInfinity);
        foreach (var point in new[] { new Vector3(-half.X, -half.Y, 0), new Vector3(half.X, -half.Y, 0), new Vector3(half.X, half.Y, 0), new Vector3(-half.X, half.Y, 0) })
        {
            var projected = Vector3.TransformCoordinate(point, matrix);
            if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y)) return false;
            var screen = new NVector2((projected.X + 1) * target.Width / 2, (1 - projected.Y) * target.Height / 2);
            minimum = NVector2.Min(minimum, screen); maximum = NVector2.Max(maximum, screen);
        }
        return true;
    }
    public Guid? Pick(NVector2 point)
    {
        if (target == null) return null;
        var hits = ElementsAtPosition(point, new NVector2(target.Width, target.Height));
        return hits.Count == 0 ? null : hits[0];
    }
    public UIElement? FindRuntime(Guid id) => runtime.GetValueOrDefault(id);
    public IEnumerable<(NVector2 Start, NVector2 End, float Value)> MarginGuides(Guid id)
    {
        if (target == null || !runtime.TryGetValue(id, out var element)) yield break;
        var half = element.RenderSize / 2; var margin = element.Margin;
        var matrix = element.WorldMatrix * RenderWorldViewProjection();
        foreach (var (point, offset, value) in new[]
        {
            (new Vector3(-half.X, 0, 0), new Vector3(-margin.Left, 0, 0), margin.Left),
            (new Vector3(half.X, 0, 0), new Vector3(margin.Right, 0, 0), margin.Right),
            (new Vector3(0, -half.Y, 0), new Vector3(0, -margin.Top, 0), margin.Top),
            (new Vector3(0, half.Y, 0), new Vector3(0, margin.Bottom, 0), margin.Bottom),
        })
        {
            var a = Vector3.TransformCoordinate(point, matrix); var b = Vector3.TransformCoordinate(point + offset, matrix);
            if (float.IsFinite(a.X) && float.IsFinite(a.Y) && float.IsFinite(b.X) && float.IsFinite(b.Y))
                yield return (new NVector2((a.X + 1) * target.Width / 2, (1 - a.Y) * target.Height / 2),
                    new NVector2((b.X + 1) * target.Width / 2, (1 - b.Y) * target.Height / 2), value);
        }
    }
    public void Render(GameTime time)
    {
        if (disposed || !Visible || target == null || RenderError != null) return;
        var command = game.GraphicsContext.CommandList; var previous = game.GraphicsDevice.ColorSpace;
        try
        {
            game.GraphicsDevice.ColorSpace = colorSpace;
            command.ResourceBarrierTransition(target, BarrierLayout.RenderTarget); command.SetRenderTargetAndViewport(depth, target);
            sceneSystem.Update(time); sceneSystem.Draw(time);
            if (presentation != null)
            {
                command.ResourceBarrierTransition(target, BarrierLayout.CopySource); command.ResourceBarrierTransition(presentation, BarrierLayout.CopyDest);
                command.Copy(target, presentation); command.ResourceBarrierTransition(presentation, BarrierLayout.ShaderResource);
            }
            else command.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
        }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); RenderError = error; Visible = false; }
        finally { game.GraphicsDevice.ColorSpace = previous; }
    }
    private void ReleaseTarget()
    {
        if (targetResources == null) return;
        targetResources.Retire(); targetResources = null;
        target = depth = presentation = null; TextureRef = default;
    }
    private PreviewTargetResources? targetResources;
    private bool sceneReleased, contentLeaseReleased;
    public void Dispose()
    {
        if (disposed) return;
        Visible = false;
        if (!sceneReleased) { sceneSystem.Dispose(); sceneReleased = true; }
        foreach (var resource in resources.ToArray())
        {
            if (!borrowedResources.ContainsKey(resource.Key)) content.Unload(resource.Value);
            resources.Remove(resource.Key);
        }
        ReleaseTarget();
        foreach (var lease in borrowedResources.ToArray())
        { lease.Value.Dispose(); borrowedResources.Remove(lease.Key); }
        if (!contentLeaseReleased) { contentLease?.Dispose(); contentLeaseReleased = true; }
        disposed = true;
    }
    private sealed class PreviewSceneSystem(Game game) : SceneSystem(game.Services) { public void InitializePreview() => LoadContent(); }
    private sealed class UIResourceCollector : AssetVisitorBase
    {
        internal readonly List<(MemberPath Path, Type Type, string Url)> References = [];
        protected override bool CanVisit(object value) => AttachedReferenceManager.GetAttachedReference(value) != null || base.CanVisit(value);
        public override void VisitObject(object value, ObjectDescriptor descriptor, bool visitMembers)
        {
            if (AttachedReferenceManager.GetAttachedReference(value) is { } reference) { References.Add((CurrentPath.Clone(), value.GetType(), reference.Url)); return; }
            base.VisitObject(value, descriptor, visitMembers);
        }
    }
}
