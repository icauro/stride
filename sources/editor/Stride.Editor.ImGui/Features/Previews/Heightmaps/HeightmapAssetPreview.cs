using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Physics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

/// <summary>WPF HeightmapPreview's actual compiled heightmap, grayscale SpriteBatch rendering and navigation.</summary>
internal sealed class HeightmapAssetPreview : IDisposable
{
    private readonly Game game;
    private readonly ImGuiRenderer renderer;
    private readonly ContentManager cache;
    private readonly Heightmap heightmap;
    private readonly Texture texture;
    private readonly HeightmapBatch batch;
    private readonly ColorSpace colorSpace;
    private Texture? target, presentation;
    private bool dirty = true, initialFit = true, disposed;
    private static readonly float[] ZoomFactors = [.02f, .05f, .083f, .125f, .167f, .2f, .25f, .333f, .5f, .667f, 1, 1.5f, 2, 3, 4, 5, 6, 8, 12, 16, 24];
    internal string SourceUrl { get; }
    internal bool CaptureReady => !disposed && Visible && !dirty && target != null && RenderError == null;
    public bool Visible { get; set; }
    public ImTextureRef TextureRef { get; private set; }
    public Exception? RenderError { get; private set; }
    public float Scale { get; private set; } = 1;
    public Vector2 Offsets { get; private set; }
    public int Width => heightmap.Size.X;
    public int Length => heightmap.Size.Y;
    public int TargetWidth => target?.Width ?? 0;
    public int TargetHeight => target?.Height ?? 0;

    public void CopyViewFrom(HeightmapAssetPreview previous)
    { Scale = previous.Scale; Offsets = previous.Offsets; initialFit = false; dirty = true; }

    public HeightmapAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent content, string url, ColorSpace colorSpace)
    {
        SourceUrl = url;
        this.game = game; this.renderer = renderer; this.colorSpace = colorSpace;
        cache = content.CreateObjectCache();
        Texture? createdTexture = null;
        Heightmap? loadedHeightmap = null;
        try
        {
            heightmap = loadedHeightmap = cache.Load<Heightmap>(url, ContentManagerLoaderSettings.StreamingDisabled);
            texture = createdTexture = heightmap.CreateTexture(game.GraphicsDevice)
                ?? throw new InvalidOperationException("The compiled heightmap has no supported height data.");
            batch = new HeightmapBatch(game.GraphicsDevice);
        }
        catch { createdTexture?.Dispose(); if (loadedHeightmap != null) cache.Unload(loadedHeightmap); throw; }
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
            next.Allocate(width, height, format, hostFormat);
            ReleaseTarget();
        }
        catch { next.Retire(); throw; }
        targetResources = next; target = next.Target; presentation = next.Presentation; TextureRef = next.Reference; RenderError = null; dirty = true;
        if (initialFit) { Fit(); Scale = Math.Min(1, Scale); initialFit = false; }
    }
    public void Fit()
    {
        if (target == null) return;
        Scale = Math.Max(.001f, Math.Min((float)target.Width / texture.Width, (float)target.Height / texture.Height));
        Offsets = Vector2.Zero; dirty = true;
    }
    public void ActualSize() { Scale = 1; dirty = true; }
    public void Zoom(bool zoomIn, Vector2? pointer = null)
    {
        var next = zoomIn ? ZoomFactors.FirstOrDefault(value => value > Scale, ZoomFactors[^1]) : ZoomFactors.LastOrDefault(value => value < Scale, ZoomFactors[0]);
        if (pointer != null && target != null) Offsets += (pointer.Value - new Vector2(target.Width, target.Height) / 2) * (1 / next - 1 / Scale);
        Scale = next; dirty = true;
    }
    public void Pan(Vector2 pixels) { Offsets += pixels / Scale; dirty = true; }
    public void Render()
    {
        if (disposed || !Visible || !dirty || target == null) return;
        var command = game.GraphicsContext.CommandList; var previous = game.GraphicsDevice.ColorSpace;
        try
        {
            game.GraphicsDevice.ColorSpace = colorSpace;
            command.ResourceBarrierTransition(target, BarrierLayout.RenderTarget); command.SetRenderTargetAndViewport(null, target);
            command.Clear(target, new Color4(.2f, .2f, .2f, 1).ToColorSpace(colorSpace));
            batch.Begin(game.GraphicsContext, SpriteSortMode.Texture, BlendStates.Opaque);
            try
            {
                batch.Draw(texture, new Vector2(target.Width, target.Height) / 2, new RectangleF(0, 0, texture.Width, texture.Height),
                    Color.White, 0, new Vector2(texture.Width, texture.Height) / 2 - Offsets, Scale, SpriteEffects.None, ImageOrientation.AsIs, swizzle: SwizzleMode.RRR1);
            }
            finally { batch.End(); }
            if (presentation != null)
            {
                command.ResourceBarrierTransition(target, BarrierLayout.CopySource); command.ResourceBarrierTransition(presentation, BarrierLayout.CopyDest);
                command.Copy(target, presentation); command.ResourceBarrierTransition(presentation, BarrierLayout.ShaderResource);
            }
            else command.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
            dirty = false; RenderError = null;
        }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); RenderError = error; dirty = false; }
        finally { game.GraphicsDevice.ColorSpace = previous; }
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
        if (disposed) return;
        Visible = false;
        if (!batchReleased) { batch.Dispose(); batchReleased = true; }
        if (!textureReleased) { texture.Dispose(); textureReleased = true; }
        if (!heightmapReleased) { cache.Unload(heightmap); heightmapReleased = true; }
        ReleaseTarget(); disposed = true;
    }
    private bool batchReleased, textureReleased, heightmapReleased;
    private sealed class HeightmapBatch(GraphicsDevice device) : SpriteBatch(device)
    {
        protected override void Destroy()
        {
            DefaultEffect.Effect.Dispose(); DefaultEffect.Dispose(); DefaultEffectSRgb.Effect.Dispose(); DefaultEffectSRgb.Dispose();
            TextureSpriteFontEffect.Effect.Dispose(); TextureSpriteFontEffect.Dispose(); base.Destroy();
        }
    }
}
