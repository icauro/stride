using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.Skyboxes;

namespace Stride.Editor.ImGui;

internal sealed class SkyboxAssetPreview : IDisposable, IRuntimeTextureConsumer
{
    private readonly Game game;
    private readonly SkyboxPreviewResources resources;
    private readonly Action<IDisposable> retire;
    private readonly GraphicsDevice device;
    private Model? model;
    private ModelAssetPreview? preview;
    private Material? material;
    internal string? CaptureSourceUrl => !disposed ? preview?.Name : null;
    internal bool CaptureReady => !disposed && Visible && RenderError == null && preview is { IsDisposed: false, RenderCount: > 0, TargetWidth: > 0 };
    private bool disposed;
    public bool UsesRuntimeTexture(Texture texture) => !disposed && (preview?.UsesRuntimeTexture(texture) == true
        || CompiledTextureConsumers.UsesTexture(model, texture) || CompiledTextureConsumers.UsesTexture(material, texture)
        || resources?.UsesRuntimeTexture(texture) == true);
    public CompiledProjectContent SourceContent { get; }
    public float Metalness { get; private set; } = 1;
    public float Glossiness { get; private set; } = 1;
    public AssetPreviewCamera Camera => preview!.Camera;
    public ImTextureRef TextureRef => preview!.TextureRef;
    public bool Visible { get => preview?.Visible == true; set { if (preview != null) preview.Visible = value; } }
    public Exception? RenderError => preview?.RenderError;
    public SkyboxAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent project, string url, bool hdr, ColorSpace colorSpace)
    {
        this.game = game; SourceContent = project; device = game.GraphicsDevice;
        var owner = game as EditorGame ?? throw new NotSupportedException("Skybox preview ownership requires the editor actor.");
        retire = resource => owner.RetirePreviewTargets(resource, device);
        resources = new SkyboxPreviewResources(project);
        try
        {
            var cache = resources.CreateCache();
            var skybox = cache.Load<Skybox>(url, ContentManagerLoaderSettings.StreamingDisabled);
            var generated = CreateMaterial(1, 1); material = generated.Material; resources.MaterialResources = generated.Resources;
            model = MaterialPreviewGeometry.CreateThumbnailModel(game.Services, material, resources.RetainBuffer);
            preview = new ModelAssetPreview(game, renderer, model, url, hdr, "StrideEditorForwardShadingEffect",
                new Vector2(0, MathUtil.DegreesToRadians(-40)), colorSpace, (scene, _) =>
                    scene.Entities.Add(new Entity("Skybox light") { new LightComponent { Type = new LightSkybox { Skybox = skybox } } }),
                retainFailedInitialization: resources.RetainFailedScene);
            resources.View = preview;
        }
        catch { retire(resources); throw; }
    }
    public void SetMaterial(float metalness, float glossiness)
    {
        EnsureDevice();
        if (!float.IsFinite(metalness) || !float.IsFinite(glossiness)) throw new ArgumentException("Preview values must be finite.");
        metalness = Math.Clamp(metalness, 0, 1); glossiness = Math.Clamp(glossiness, 0, 1);
        if (Metalness == metalness && Glossiness == glossiness) return;
        var next = CreateMaterial(metalness, glossiness);
        var previous = material;
        var previousResources = resources.MaterialResources;
        try
        {
            model!.Materials[0].Material = next.Material;
            if (previousResources != null) retire(previousResources);
        }
        catch { model!.Materials[0].Material = previous; retire(next.Resources); throw; }
        material = next.Material; resources.MaterialResources = next.Resources; Metalness = metalness; Glossiness = glossiness;
    }
    private (Material Material, MaterialContentCacheResources Resources) CreateMaterial(float metalness, float glossiness)
    {
        var generated = new MaterialContentCacheResources(SourceContent);
        try
        {
            var result = Material.New(device, new MaterialDescriptor
            {
                Attributes = new MaterialAttributes
                {
                    Diffuse = new MaterialDiffuseMapFeature { DiffuseMap = new ComputeColor(Color4.White) },
                    MicroSurface = new MaterialGlossinessMapFeature { GlossinessMap = new ComputeFloat(glossiness) },
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                    Specular = new MaterialMetalnessMapFeature { MetalnessMap = new ComputeFloat(metalness) },
                    SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
                },
            }, generated.Content);
            return (result, generated);
        }
        catch { retire(generated); throw; }
    }
    private void EnsureDevice()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(device, game.GraphicsDevice)) throw new InvalidOperationException("The skybox preview device changed before use.");
    }
    public void Resize(int width, int height) { EnsureDevice(); preview!.Resize(width, height); }
    public void ResetCamera() { EnsureDevice(); preview!.ResetCamera(); }
    public void Render(GameTime time) { if (disposed) return; EnsureDevice(); preview?.Render(time); }
    public void Dispose()
    {
        if (disposed) return;
        Visible = false; retire(resources);
        preview = null; model = null; material = null;
        disposed = true;
    }
}
