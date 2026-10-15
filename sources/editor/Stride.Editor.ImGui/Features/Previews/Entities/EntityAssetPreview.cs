using Stride.Core.Assets;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Editor.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Assets.Rendering;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.SpriteStudio.Runtime;

namespace Stride.Editor.ImGui;

/// <summary>Actual compiled entity preview with WPF PreviewFromEntity composition and camera behavior.</summary>
internal sealed class EntityAssetPreview : IDisposable, IRuntimeTextureConsumer
{
    private readonly Game game;
    private readonly ImGuiRenderer renderer;
    private readonly ContentManager cache;
    private readonly Action unload;
    private readonly PreviewSceneSystem sceneSystem;
    private readonly Entity root;
    private readonly ColorSpace colorSpace;
    private Texture? target, depth, presentation;
    internal bool CaptureReady => !disposed && Visible && RenderError == null && target != null && rendered;
    private bool rendered;
    private bool disposed;
    public bool UsesRuntimeTexture(Texture texture) => !disposed && (CompiledTextureConsumers.UsesTexture(root, texture)
        || CompiledTextureConsumers.UsesTexture(sceneSystem?.SceneInstance?.RootScene, texture)
        || CompiledTextureConsumers.UsesTexture(cache, texture));
    public AssetPreviewCamera Camera { get; }
    public ImTextureRef TextureRef { get; private set; }
    public bool Visible { get; set; }
    public Exception? RenderError { get; private set; }
    public string Name { get; }

    public static EntityAssetPreview Prefab(Game game, ImGuiRenderer renderer, CompiledProjectContent project, string url, bool hdr, ColorSpace colorSpace, PackageTypeScope? scope = null)
        => Load(game, renderer, project, url, hdr, colorSpace, scope, cache =>
        {
            var prefab = cache.Load<Prefab>(url, ContentManagerLoaderSettings.StreamingDisabled);
            try
            {
                var root = new Entity("Preview Entity of prefab: " + url);
                foreach (var entity in prefab.Entities) root.AddChild(entity);
                return (root, () => cache.Unload(prefab));
            }
            catch { cache.Unload(prefab); throw; }
        });

    public static EntityAssetPreview PrefabModel(Game game, ImGuiRenderer renderer, CompiledProjectContent project, string url, bool hdr, ColorSpace colorSpace, PackageTypeScope? scope = null)
        => Load(game, renderer, project, url, hdr, colorSpace, scope, cache =>
        {
            var model = cache.Load<Model>(url, ContentManagerLoaderSettings.StreamingDisabled);
            return (new Entity("Preview Entity of model: " + url) { new ModelComponent { Model = model } }, () => cache.Unload(model));
        });

    public static EntityAssetPreview SpriteStudio(Game game, ImGuiRenderer renderer, CompiledProjectContent project, string url, bool hdr, ColorSpace colorSpace, PackageTypeScope? scope = null)
        => Load(game, renderer, project, url, hdr, colorSpace, scope, cache =>
        {
            var sheet = cache.Load<SpriteStudioSheet>(url, ContentManagerLoaderSettings.StreamingDisabled);
            return (new Entity("Preview entity of SpriteStudio sheet: " + url) { new SpriteStudioComponent { Sheet = sheet } }, () => cache.Unload(sheet));
        });

    private static EntityAssetPreview Load(Game game, ImGuiRenderer renderer, CompiledProjectContent project, string url, bool hdr,
        ColorSpace colorSpace, PackageTypeScope? scope, Func<ContentManager, (Entity Root, Action Unload)> load)
    {
        var cache = project.CreateObjectCache();
        Action? unload = null;
        try
        {
            var resource = load(cache);
            unload = resource.Unload;
            return new EntityAssetPreview(game, renderer, cache, resource.Root, resource.Unload, url, hdr, colorSpace, scope);
        }
        catch { unload?.Invoke(); throw; }
    }

    private EntityAssetPreview(Game game, ImGuiRenderer renderer, ContentManager cache, Entity root, Action unload, string name, bool hdr, ColorSpace colorSpace, PackageTypeScope? scope = null)
    {
        this.game = game; this.renderer = renderer; this.cache = cache; this.root = root; this.unload = unload; this.colorSpace = colorSpace; Name = name;
        var camera = new Entity("Preview Camera") { new CameraComponent() };
        Camera = new AssetPreviewCamera(camera);
        AssetPreviewCamera.AttachLighting(camera, hdr);
        var scene = new Scene(); scene.Entities.Add(camera); scene.Entities.Add(root);
        PreviewSceneSystem? created = null;
        try
        {
            created = new PreviewSceneSystem(game);
            created.GraphicsCompositor.Dispose();
            created.GraphicsCompositor = GraphicsCompositorHelper.CreateDefault(hdr, "StrideEditorForwardShadingEffect", camera.Get<CameraComponent>(),
                hdr ? new Color(61, 61, 61, 255) : new Color(51, 51, 51, 255));
            var opaque = created.GraphicsCompositor.RenderStages.First(stage => stage.Name == "Opaque");
            var transparent = created.GraphicsCompositor.RenderStages.First(stage => stage.Name == "Transparent");
            EditorPackageRenderFeatures.Add(created.GraphicsCompositor, opaque, transparent, scope);
            created.SceneInstance = new SceneInstance(game.Services, scene, ExecutionMode.Preview);
            created.InitializePreview(); sceneSystem = created;
            ResetCamera();
        }
        catch { created?.Dispose(); throw; }
    }

    public void ResetCamera()
    {
        Camera.ResetViewAngle(); Camera.ResetViewTarget(root.CalculateBoundSphere()); RenderError = null;
    }

    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        width = Math.Clamp(width, 1, 4096); height = Math.Clamp(height, 1, 4096);
        if (target?.Width == width && target.Height == height) return;
        var format = colorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var hostFormat = game.GraphicsDevice.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var next = new PreviewTargetResources(game, renderer);
        try
        {
            next.Allocate(width, height, format, hostFormat, depth: true);
            ReleaseTarget();
        }
        catch { next.Retire(); throw; }
        rendered = false;
        targetResources = next; target = next.Target; depth = next.Depth; presentation = next.Presentation; TextureRef = next.Reference; RenderError = null;
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
            rendered = true;
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

    public void Dispose()
    {
        if (disposed) return;
        Visible = false;
        if (!sceneReleased) { sceneSystem.Dispose(); sceneReleased = true; }
        if (!contentReleased) { unload(); contentReleased = true; }
        ReleaseTarget(); disposed = true;
    }
    private bool sceneReleased, contentReleased;

    private sealed class PreviewSceneSystem(Game game) : SceneSystem(game.Services)
    {
        public void InitializePreview() => LoadContent();
    }
}
