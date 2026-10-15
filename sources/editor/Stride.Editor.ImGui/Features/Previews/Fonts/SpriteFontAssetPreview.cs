using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Assets.SpriteFont;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed class SpriteFontAssetPreview : IDisposable, IRuntimeTextureConsumer, IEditorResourceRetirementDrain
{
    private sealed class FontBatch(GraphicsDevice device) : SpriteBatch(device)
    {
        protected override void Destroy()
        {
            DefaultEffect.Effect.Dispose(); DefaultEffect.Dispose(); DefaultEffectSRgb.Effect.Dispose(); DefaultEffectSRgb.Dispose();
            TextureSpriteFontEffect.Effect.Dispose(); TextureSpriteFontEffect.Dispose(); base.Destroy();
        }
    }
    private readonly Game game;
    private readonly ImGuiRenderer renderer;
    private readonly CompiledFontContent content;
    private readonly IDisposable? contentLease;
    private readonly string sourceUrl;
    private readonly Asset sourceAsset;
    public CompiledProjectContent SourceContent => content.Bundle;
    public bool IsDisposed => disposed;
    public bool IsRetirementDrainComplete => content.IsRetirementDrainComplete;
    public bool PumpRetirementDrain() => content.PumpRetirementDrain();
    public bool UsesRuntimeTexture(Texture texture) => !disposed && content.UsesRuntimeTexture(texture);
    private readonly SpriteBatch batch;
    private readonly BlendStateDescription blend;
    private readonly ColorSpace colorSpace;
    private Texture? target, presentation;
    private bool dirty = true, disposed;
    private string sample = "";
    private float navigationScale = 1;
    public ImTextureRef TextureRef { get; private set; }
    internal bool CaptureReady => !disposed && Visible && !dirty && target != null && RenderError == null;
    public bool Visible { get; set; }
    public Exception? RenderError { get; private set; }
    public Vector2 Offsets { get; private set; }
    public int TargetWidth => target?.Width ?? 0;
    public int TargetHeight => target?.Height ?? 0;
    public string Sample { get => sample; set { if (sample != value) { sample = value; dirty = true; } } }
    public SpriteFontAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent bundle, string url, Asset asset, ColorSpace colorSpace, IDisposable? contentLease = null)
    {
        this.game = game; this.renderer = renderer; this.colorSpace = colorSpace; this.contentLease = contentLease;
        sourceUrl = url; sourceAsset = AssetCloner.Clone(asset);
        content = new CompiledFontContent(game, bundle, url);
        try
        {
            batch = new FontBatch(game.GraphicsDevice);
            var premultiplied = asset is SpriteFontAsset font ? font.FontType.IsPremultiplied : ((PrecompiledSpriteFontAsset)asset).IsPremultiplied;
            blend = premultiplied ? BlendStates.AlphaBlend : BlendStates.NonPremultiplied;
        }
        catch { content.Dispose(); throw; }
    }
    internal void PrepareFontRetirement() => content.RetireGraphicsResources();
    internal SpriteFontAssetPreview Recreate(ImGuiRenderer nextRenderer, IDisposable? nextLease)
    {
        var next = new SpriteFontAssetPreview(game, nextRenderer, SourceContent, sourceUrl, sourceAsset, colorSpace, nextLease);
        next.CopyViewFrom(this); return next;
    }
    internal string SourceUrl => sourceUrl;
    public void Pan(Vector2 delta) { Offsets += delta / navigationScale; dirty = true; }
    public void NavigateWheel(bool increase, Vector2 pointer)
    {
        var factors = Stride.Core.Presentation.Core.Utils.ZoomFactors;
        var next = (float)(increase ? factors[^1] : factors[0]);
        if (increase)
        { foreach (var factor in factors) if ((float)factor > navigationScale) { next = (float)factor; break; } }
        else
        { for (var index = factors.Length - 1; index >= 0; --index) if ((float)factors[index] < navigationScale) { next = (float)factors[index]; break; } }
        if (target == null || next == navigationScale) return;
        // FontPreview inherits the native sprite navigation scale, while its
        // glyph renderer intentionally takes only offsets (not SpriteScale).
        Offsets += (pointer - new Vector2(target.Width, target.Height) / 2) * (1 / next - 1 / navigationScale);
        navigationScale = next; dirty = true;
    }
    public void CopyViewFrom(SpriteFontAssetPreview previous)
    { sample = previous.sample; Offsets = previous.Offsets; navigationScale = previous.navigationScale; dirty = true; }
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
            next.Allocate(width, height, format, hostFormat);
            ReleaseTarget();
        }
        catch { next.Retire(); throw; }
        targetResources = next; target = next.Target; presentation = next.Presentation; TextureRef = next.Reference; RenderError = null; dirty = true;
    }
    public void Render()
    {
        if (!Visible || !dirty || target == null || disposed) return;
        var command = game.GraphicsContext.CommandList; var previousSpace = game.GraphicsDevice.ColorSpace;
        try
        {
            content.Draw(); game.GraphicsDevice.ColorSpace = colorSpace;
            command.ResourceBarrierTransition(target, BarrierLayout.RenderTarget); command.SetRenderTargetAndViewport(null, target);
            command.Clear(target, new Color4(.2f, .2f, .2f, 1).ToColorSpace(colorSpace));
            batch.Begin(game.GraphicsContext, SpriteSortMode.Texture, blend, null, null, null,
                content.Font.FontType == SpriteFontType.SDF ? batch.TextureSpriteFontEffect : null);
            try { SpriteFontPreviewRenderer.DrawPreview(batch, content.Font, sample, new(target.Width, target.Height), Offsets); }
            finally { batch.End(); }
            if (presentation != null)
            {
                command.ResourceBarrierTransition(target, BarrierLayout.CopySource); command.ResourceBarrierTransition(presentation, BarrierLayout.CopyDest);
                command.Copy(target, presentation); command.ResourceBarrierTransition(presentation, BarrierLayout.ShaderResource);
            }
            else command.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
            RenderError = null; dirty = false;
        }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); RenderError = error; dirty = false; }
        finally { game.GraphicsDevice.ColorSpace = previousSpace; }
    }
    private void ReleaseTarget()
    {
        if (targetResources == null) return;
        targetResources.Retire(); targetResources = null;
        target = presentation = null; TextureRef = default;
    }
    private PreviewTargetResources? targetResources;
    public void Dispose()
    {
        if (disposed) return; Visible = false;
        if (!batchReleased) { batch.Dispose(); batchReleased = true; }
        if (!contentReleased) { content.Dispose(); contentReleased = true; }
        ReleaseTarget();
        if (!leaseReleased) { contentLease?.Dispose(); leaseReleased = true; }
        disposed = true;
    }
    private bool batchReleased, contentReleased, leaseReleased;
}
