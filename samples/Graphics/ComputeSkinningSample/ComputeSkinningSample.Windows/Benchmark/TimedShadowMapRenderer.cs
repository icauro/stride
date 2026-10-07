using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;

sealed class TimedShadowMapRenderer(IShadowMapRenderer inner, GpuTimers timers) : IShadowMapRenderer
{
    public RenderSystem RenderSystem { get => inner.RenderSystem; set => inner.RenderSystem = value; }
    public HashSet<RenderView> RenderViewsWithShadows => inner.RenderViewsWithShadows;
    public List<ILightShadowMapRenderer> Renderers => inner.Renderers;
    public int ViewCount { get; private set; }
    public int MeshSubmissions { get; private set; }
    public LightShadowMapTexture FindShadowMap(RenderView view, RenderLight light) => inner.FindShadowMap(view, light);
    public void Collect(RenderContext context, Dictionary<RenderView, ForwardLightingRenderFeature.RenderViewLightData> data) => inner.Collect(context, data);
    public void PrepareAtlasAsRenderTargets(CommandList commands) => inner.PrepareAtlasAsRenderTargets(commands);
    public void PrepareAtlasAsShaderResourceViews(CommandList commands) => inner.PrepareAtlasAsShaderResourceViews(commands);
    public void Flush(RenderDrawContext context) => inner.Flush(context);
    public void Draw(RenderDrawContext context)
    {
        var views = RenderSystem.Views.OfType<ShadowMapRenderView>().Where(view => view.RenderView == context.RenderContext.RenderView).ToArray();
        ViewCount = views.Length;
        MeshSubmissions = views.Sum(view => view.RenderObjects.OfType<RenderMesh>().Count());
        using (timers.BeginRegion("ShadowMaps")) inner.Draw(context);
    }
}
