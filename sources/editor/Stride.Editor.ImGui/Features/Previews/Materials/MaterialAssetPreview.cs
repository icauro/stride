using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Assets.Materials;
using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

/// <summary>Compiled material hosted on the same engine primitive and preview scene paths as WPF.</summary>
internal sealed class MaterialAssetPreview : IDisposable, IMaterialPreviewContentOwner, IRuntimeTextureConsumer
{
    private readonly Game game;
    private readonly ImGuiRenderer renderer;
    private readonly ContentManager content;
    private readonly Material material;
    private readonly bool hdr;
    private readonly ColorSpace colorSpace;
    private readonly Action<IDisposable> retire;
    private IDisposable? contentLease;
    private MaterialPrimitiveResources? primitiveResources;
    private ModelAssetPreview? preview;
    private Exception? resizeError;
    private (int Width, int Height) failedResize;
    internal string? CaptureSourceUrl => !disposed ? Name : null;
    internal bool CaptureReady => !disposed && Visible && RenderError == null && preview is { IsDisposed: false, RenderCount: > 0, TargetWidth: > 0 };
    private bool disposed;
    public string Name { get; }
    public CompiledProjectContent SourceContent { get; }
    public EditorMaterialPrimitive Primitive { get; private set; }
    public AssetPreviewCamera Camera => preview!.Camera;
    public ImTextureRef TextureRef => preview!.TextureRef;
    public bool Visible { get => preview?.Visible == true; set { if (preview != null) preview.Visible = value; } }
    public Exception? RenderError { get; private set; }
    public bool UsesRuntimeTexture(Texture texture)
        => !disposed && (CompiledTextureConsumers.UsesTexture(material, texture)
            || CompiledTextureConsumers.UsesTexture(content, texture)
            || preview?.UsesRuntimeTexture(texture) == true || primitiveResources?.UsesRuntimeTexture(texture) == true);
    internal void ReplaceTexture(string url, Texture texture) => CompiledTextureConsumers.Replace(material, url, texture);

    public MaterialAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent project, string url,
        RenderingMode mode, ColorSpace colorSpace, Action<IDisposable> retire)
    {
        this.game = game; this.renderer = renderer; Name = url; SourceContent = project; hdr = mode == RenderingMode.HDR; this.colorSpace = colorSpace;
        this.retire = retire ?? throw new ArgumentNullException(nameof(retire));
        contentLease = project.AcquireLease();
        try
        {
            content = project.CreateObjectCache();
            material = content.Load<Material>(url, ContentManagerLoaderSettings.StreamingDisabled);
            SetPrimitive(EditorMaterialPrimitive.Sphere);
        }
        catch
        {
            retire(this);
            throw;
        }
    }
    private MaterialAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent source, ContentManager content, Material material, string url,
        RenderingMode mode, ColorSpace colorSpace, Action<IDisposable> retire, IDisposable contentLease)
    {
        this.game = game; this.renderer = renderer; this.content = content; this.material = material;
        this.contentLease = contentLease;
        Name = url; SourceContent = source; hdr = mode == RenderingMode.HDR; this.colorSpace = colorSpace;
        this.retire = retire ?? throw new ArgumentNullException(nameof(retire));
        try { SetPrimitive(EditorMaterialPrimitive.Sphere); }
        catch { retire(this); throw; }
    }
    public static MaterialAssetPreview FromAuthored(Game game, ImGuiRenderer renderer, CompiledProjectContent project,
        MaterialAsset asset, IAssetFinder finder, string url, RenderingMode mode, RenderingSettings settings, Action<IDisposable> retire)
    {
        ArgumentNullException.ThrowIfNull(retire);
        var generationResources = new MaterialContentCacheResources(project);
        var generationCache = generationResources.Content;
        try
        {
            var result = MaterialPreviewGeneration.Generate(asset, finder, generationCache, settings.DefaultGraphicsProfile, settings.ColorSpace, url);
            if (result.HasErrors) throw new InvalidOperationException(result.ToText());
            // A separate loading cache is essential: Save retains its input object, whereas
            // this preview needs the GPU resources produced by the normal content serializers.
            var generatedUrl = "__imgui_material_preview__/" + asset.Id;
            generationCache.Save(generatedUrl, result.Material);
            MaterialContentCacheResources? loadingResources = new(project);
            var cache = loadingResources.Content;
            try
            {
                var compiled = cache.Load<Material>(generatedUrl, ContentManagerLoaderSettings.StreamingDisabled);
                // The constructor takes this real cache/reference before staging
                // its primitive; its failure path retains that owner itself.
                var previewLease = loadingResources.TransferLease(); loadingResources = null;
                return new MaterialAssetPreview(game, renderer, project, cache, compiled, url, mode, settings.ColorSpace, retire, previewLease);
            }
            finally { if (loadingResources != null) retire(loadingResources); }
        }
        finally { retire(generationResources); }
    }
    public void SetPrimitive(EditorMaterialPrimitive primitive)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (preview != null && Primitive == primitive) return;
        var next = new MaterialPrimitiveResources(SourceContent, content);
        try
        {
            next.RetainMaterial(material);
            MaterialPreviewGeometry.PopulatePreviewModel(game.Services, material, primitive, next.Model, next.RetainBuffer);
            next.View = new ModelAssetPreview(game, renderer, next.Model, Name, hdr, MaterialPreviewGeometry.EffectName,
                new Vector2(MathUtil.Pi, 0), colorSpace, retainFailedInitialization: next.RetainFailedInitialization);
            if (preview != null)
            {
                next.View.Camera.SetViewAngles(preview.Camera.Yaw, preview.Camera.Pitch);
                if (preview.TargetWidth > 0 && preview.TargetHeight > 0) next.View.Resize(preview.TargetWidth, preview.TargetHeight);
                next.View.Visible = preview.Visible;
            }
        }
        catch { retire(next); throw; }
        if (primitiveResources != null)
        {
            var visible = preview!.Visible; preview.Visible = false;
            try { retire(primitiveResources); }
            catch { preview.Visible = visible; retire(next); throw; }
        }
        primitiveResources = next; preview = next.View; Primitive = primitive; RenderError = null; resizeError = null;
    }
    public void Resize(int width, int height)
    {
        if (resizeError != null && failedResize == (width, height)) throw resizeError;
        try { preview!.Resize(width, height); resizeError = null; }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); resizeError = error; failedResize = (width, height); throw; }
    }
    public void ResetCamera() { preview!.ResetCamera(); RenderError = null; }
    public void Render(GameTime time)
    {
        if (RenderError != null || preview == null) return;
        try
        {
            preview.Render(time);
            if (preview.RenderError is { } error) { RenderError = error; preview.Visible = false; }
        }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); RenderError = error; preview.Visible = false; }
    }
    public void Dispose()
    {
        if (disposed) return;
        Visible = false;
        if (primitiveResources != null) { retire(primitiveResources); primitiveResources = null; preview = null; }
        if (!materialReleased)
        { if (material == null) content?.UnloadAll(); else content.Unload(material); materialReleased = true; }
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
        disposed = true;
    }
    private bool materialReleased;
}
