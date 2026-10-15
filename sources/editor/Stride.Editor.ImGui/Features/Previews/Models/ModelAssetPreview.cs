using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Graphics;
using Stride.Games;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal enum ModelPreviewBackdrop { Default, Dark, Light }

/// <summary>Compiled model preview using the real SceneSystem and shared WPF preview behavior.</summary>
internal sealed partial class ModelAssetPreview : IDisposable, IRuntimeTextureConsumer
{
    private readonly Game game;
    private readonly ImGuiRenderer renderer;
    private readonly ContentManager? content;
    private readonly ColorSpace? previewColorSpace;
    private readonly Model model;
    private readonly PreviewSceneSystem sceneSystem;
    private readonly Entity cameraEntity;
    private readonly Entity modelEntity;
    private readonly BoundingSphere bounds;
    private readonly ForwardRenderer previewRenderer;
    private readonly Color4 defaultBackground;
    private Texture? target, depth, presentation;
    private PreviewTargetResources? targetResources;
    private Exception? resizeError;
    private (int Width, int Height) failedResize;
    private readonly System.Diagnostics.Stopwatch renderedLifetime = new();
    public AssetPreviewCamera Camera { get; }
    public string Name { get; }
    public bool IsProcedural { get; }
    public ModelPreviewBackdrop Backdrop { get; private set; }
    public ImTextureRef TextureRef { get; private set; }
    internal int TargetWidth => target?.Width ?? 0;
    internal int TargetHeight => target?.Height ?? 0;
    public int RenderCount { get; private set; }
    public int DrawCalls { get; private set; }
    public double RenderAgeSeconds => renderedLifetime.Elapsed.TotalSeconds;
    public bool Visible { get; set; }
    public bool IsDisposed { get; private set; }
    public Exception? RenderError { get; private set; }
    internal void ReplaceTexture(string url, Texture texture)
    {
        foreach (var instance in model.Materials)
            if (instance.Material != null) CompiledTextureConsumers.Replace(instance.Material, url, texture);
    }
    public int MeshCount { get; }
    public int MaterialPassCount { get; }
    public long VertexBytes { get; }
    public string Details { get; }
    public CompiledProjectContent? SourceContent { get; private set; }
    public Matrix CameraMatrix => cameraEntity.Transform.LocalMatrix;
    internal BoundingSphere FramingBounds => bounds;
    internal Vector3 ModelSize => model.BoundingBox.Maximum - model.BoundingBox.Minimum;
    internal string? MaterialUrl => Stride.Core.Serialization.AttachedReferenceManager.GetAttachedReference(model.Materials.FirstOrDefault()?.Material)?.Url;
    internal bool CastsShadows => model.Materials.FirstOrDefault()?.IsShadowCaster == true;
    internal bool ModelFitsCamera => modelEntity.Get<ModelComponent>().BoundingBox.GetCorners().All(corner =>
    {
        var clip = Vector3.TransformCoordinate(corner, cameraEntity.Get<CameraComponent>().ViewProjectionMatrix);
        return float.IsFinite(clip.X) && float.IsFinite(clip.Y) && float.IsFinite(clip.Z)
            && Math.Abs(clip.X) <= 1.0001f && Math.Abs(clip.Y) <= 1.0001f && clip.Z >= 0 && clip.Z <= 1;
    });
    public ModelAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent project, string url, bool hdr, bool procedural = false,
        Vector2? defaultAngles = null, ColorSpace? colorSpace = null)
        : this(game, renderer, LoadModel(project, url), url, hdr, procedural, defaultAngles, "StrideEditorForwardShadingEffect", 1.35f, colorSpace)
    { SourceContent = project; }
    internal ModelAssetPreview(Game game, ImGuiRenderer renderer, Model model, string name, bool hdr, string effectName,
        Vector2 defaultAngles, ColorSpace colorSpace, Action<Scene, Entity>? setupLighting = null,
        Action<IDisposable>? retainFailedInitialization = null)
        : this(game, renderer, (null, model), name, hdr, false, defaultAngles, effectName, 1f, colorSpace, setupLighting, retainFailedInitialization)
    { }
    private static (ContentManager? Content, Model Model) LoadModel(CompiledProjectContent project, string url)
    {
        var cache = project.CreateObjectCache();
        return (cache, cache.Load<Model>(url, ContentManagerLoaderSettings.StreamingDisabled));
    }
    private ModelAssetPreview(Game game, ImGuiRenderer renderer, (ContentManager? Content, Model Model) loaded, string url,
        bool hdr, bool procedural, Vector2? defaultAngles, string effectName, float framingPadding, ColorSpace? colorSpace,
        Action<Scene, Entity>? setupLighting = null, Action<IDisposable>? retainFailedInitialization = null)
    {
        this.game = game; this.renderer = renderer; Name = url; IsProcedural = procedural;
        previewColorSpace = colorSpace;
        defaultBackground = hdr ? new Color(61, 61, 61, 255) : new Color(51, 51, 51, 255);
        content = loaded.Content; model = loaded.Model;
        PreviewSceneSystem? created = null;
        try
        {
            MeshCount = model.Meshes.Count;
            MaterialPassCount = model.Materials.Sum(material => material.Material?.Passes.Count ?? 0);
            VertexBytes = model.Meshes.Sum(mesh => mesh.Draw.VertexBuffers.Sum(binding => (long)binding.Buffer.SizeInBytes));
            Details = $"Compiled {(procedural ? "procedural model" : "project model")} · {MeshCount} {(MeshCount == 1 ? "mesh" : "meshes")} · {MaterialPassCount} {(MaterialPassCount == 1 ? "material pass" : "material passes")}";
            var entity = modelEntity = new Entity("Preview Entity of model: " + url) { new ModelComponent { Model = model } };
            cameraEntity = new Entity("Preview Camera") { new CameraComponent() };
            Camera = new AssetPreviewCamera(cameraEntity) { FramingPadding = framingPadding };
            if (defaultAngles is { } angles) { Camera.DefaultYaw = angles.X; Camera.DefaultPitch = angles.Y; }
            if (setupLighting == null) AssetPreviewCamera.AttachLighting(cameraEntity, hdr);
            bounds = ModelPreviewBounds.Calculate(entity);
            ResetCamera();
            var scene = new Scene(); scene.Entities.Add(cameraEntity); scene.Entities.Add(entity);
            setupLighting?.Invoke(scene, cameraEntity);
            created = new PreviewSceneSystem(game);
            created.GraphicsCompositor.Dispose();
            created.GraphicsCompositor = GraphicsCompositorHelper.CreateDefault(hdr, effectName, cameraEntity.Get<CameraComponent>(),
                defaultBackground);
            previewRenderer = (ForwardRenderer)created.GraphicsCompositor.SingleView;
            created.SceneInstance = new SceneInstance(game.Services, scene, ExecutionMode.Preview);
            created.InitializePreview(); sceneSystem = created;
        }
        catch
        {
            if (created != null)
            {
                if (retainFailedInitialization != null) retainFailedInitialization(created);
                else created.Dispose();
            }
            content?.Unload(model); throw;
        }
    }
    public void ResetCamera() { Camera.ResetViewAngle(); Camera.ResetViewTarget(bounds); RenderError = null; }
    public void SetBackdrop(ModelPreviewBackdrop backdrop)
    {
        if (Backdrop == backdrop) return;
        Backdrop = backdrop;
        previewRenderer.Clear.Color = backdrop switch
        {
            ModelPreviewBackdrop.Dark => new Color(12, 12, 12, 255),
            ModelPreviewBackdrop.Light => new Color(191, 191, 191, 255),
            _ => defaultBackground,
        };
        // Test captures must allow HDR adaptation to the new viewing background.
        renderedLifetime.Restart();
    }
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        width = Math.Clamp(width, 1, 4096); height = Math.Clamp(height, 1, 4096);
        if (resizeError != null && failedResize == (width, height)) throw resizeError;
        if (target?.Width == width && target.Height == height) return;
        var format = (previewColorSpace ?? game.GraphicsDevice.ColorSpace) == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var hostFormat = game.GraphicsDevice.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var next = new PreviewTargetResources(game, renderer);
        try
        {
            next.Allocate(width, height, format, hostFormat, depth: true);
            ReleaseTarget();
        }
        catch (Exception error)
        {
            next.Retire();
            EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error);
            resizeError = error; failedResize = (width, height); throw;
        }
        targetResources = next; target = next.Target; depth = next.Depth; presentation = next.Presentation; TextureRef = next.Reference;
        resizeError = null;
    }
    public void Render(GameTime time)
    {
        if (RenderError != null) { DrawCalls = 0; return; }
        try { RenderCore(time); }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); RenderError = error; Visible = false; renderedLifetime.Stop(); }
    }
    private void RenderCore(GameTime time)
    {
        DrawCalls = 0;
        if (!Visible || target == null) { renderedLifetime.Stop(); return; }
        renderedLifetime.Start();
        var command = game.GraphicsContext.CommandList;
        command.SetRenderTargetAndViewport(depth, target);
        var before = game.GraphicsDevice.FrameDrawCalls;
        var hostSpace = game.GraphicsDevice.ColorSpace;
        try
        {
            game.GraphicsDevice.ColorSpace = previewColorSpace ?? hostSpace;
            sceneSystem.Update(time); sceneSystem.Draw(time);
        }
        finally { game.GraphicsDevice.ColorSpace = hostSpace; }
        DrawCalls = checked((int)(game.GraphicsDevice.FrameDrawCalls - before));
        if (presentation != null)
        {
            command.ResourceBarrierTransition(target, BarrierLayout.CopySource);
            command.ResourceBarrierTransition(presentation, BarrierLayout.CopyDest); command.Copy(target, presentation);
            command.ResourceBarrierTransition(presentation, BarrierLayout.ShaderResource);
        }
        else command.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
        RenderCount++;
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        Visible = false;
        if (!sceneReleased) { sceneSystem.Dispose(); sceneReleased = true; }
        if (!modelReleased) { content?.Unload(model); modelReleased = true; }
        ReleaseTarget();
        IsDisposed = true;
    }
    private bool sceneReleased, modelReleased;
    private void ReleaseTarget()
    {
        if (targetResources == null) return;
        targetResources.Retire(); targetResources = null;
        target = depth = presentation = null; TextureRef = default;
    }
    private sealed class PreviewSceneSystem(Game game) : SceneSystem(game.Services)
    {
        public void InitializePreview() => LoadContent();
    }
}
