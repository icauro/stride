using Hexa.NET.ImGui;
using Stride.Assets.Sprite;
using Stride.Assets.Textures;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed class SpriteSheetAssetPreview : IDisposable, IRuntimeTextureConsumer
{
    private sealed class PreviewBatch(GraphicsDevice device) : SpriteBatch(device)
    {
        protected override void Destroy()
        {
            DefaultEffect.Effect.Dispose(); DefaultEffect.Dispose(); DefaultEffectSRgb.Effect.Dispose(); DefaultEffectSRgb.Dispose();
            TextureSpriteFontEffect.Effect.Dispose(); TextureSpriteFontEffect.Dispose(); base.Destroy();
        }
    }
    private readonly Game game;
    private readonly ImGuiRenderer renderer;
    private readonly ContentManager? cache;
    private IDisposable? contentLease;
    public CompiledProjectContent SourceContent { get; }
    public bool IsDisposed => disposed;
    private readonly SpriteSheet sheet;
    private readonly Texture[] textures = [];
    private readonly SpriteBatch batch;
    private readonly BlendStateDescription blend;
    private readonly ColorSpace colorSpace;
    private Texture? target, presentation;
    private bool dirty = true, initialFit = true, disposed;
    private bool spriteTextures;
    private int frame;
    private static readonly float[] ZoomFactors = [.02f, .05f, .083f, .125f, .167f, .2f, .25f, .333f, .5f, .667f, 1, 1.5f, 2, 3, 4, 5, 6, 8, 12, 16, 24];
    public bool UsesRuntimeTexture(Texture texture) => !disposed && (textures.Any(value => ReferenceEquals(value, texture))
        || CompiledTextureConsumers.UsesTexture(sheet, texture) || CompiledTextureConsumers.UsesTexture(cache, texture));
    internal string SourceUrl { get; }
    internal bool CaptureReady => !disposed && Visible && !dirty && target != null && RenderError == null;
    public bool Visible { get; set; }
    public ImTextureRef TextureRef { get; private set; }
    public Exception? RenderError { get; private set; }
    public float Scale { get; private set; } = 1;
    public Vector2 Offsets { get; private set; }
    public int TargetWidth => target?.Width ?? 0;
    public int TargetHeight => target?.Height ?? 0;
    public int FrameCount => spriteTextures ? textures.Length : sheet.Sprites.Count;
    public int CurrentFrame { get => frame; set { var next = Math.Clamp(value, 0, Math.Max(0, FrameCount - 1)); if (next != frame) { frame = next; dirty = true; } } }
    public bool SpriteTextures
    {
        get => spriteTextures;
        set { if (spriteTextures != value) { spriteTextures = value; CurrentFrame = frame; Fit(); dirty = true; } }
    }
    public SpriteSheetAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent content, string url, SpriteSheetAsset asset, ColorSpace colorSpace)
    {
        SourceUrl = url;
        this.game = game; this.renderer = renderer; this.colorSpace = colorSpace; SourceContent = content;
        var owner = game as EditorGame ?? throw new NotSupportedException("Sprite preview retirement requires the editor ownership actor.");
        try
        {
            contentLease = content.AcquireLease();
            cache = content.CreateObjectCache(); sheet = cache.Load<SpriteSheet>(url, ContentManagerLoaderSettings.StreamingDisabled);
            textures = sheet.Sprites.Select(sprite => sprite.Texture).Distinct().ToArray();
            batch = new PreviewBatch(game.GraphicsDevice);
            blend = asset.Alpha == AlphaFormat.None ? BlendStates.Opaque : asset.PremultiplyAlpha ? BlendStates.AlphaBlend : BlendStates.NonPremultiplied;
        }
        catch { owner.RetirePreviewTargets(this, game.GraphicsDevice); throw; }
    }
    private Vector2 MaximumSize()
    {
        var size = Vector2.Zero;
        foreach (var sprite in sheet.Sprites)
        {
            var current = spriteTextures ? sprite.Texture == null ? Vector2.Zero : new Vector2(sprite.Texture.Width, sprite.Texture.Height) : sprite.SizeInPixels;
            size = Vector2.Max(size, current);
        }
        return size;
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
        if (initialFit)
        {
            Fit();
            // Native first attachment uses actual pixels when all frames fit;
            // explicit Fit and display-mode changes still fill the preview area.
            Scale = Math.Min(1, Scale);
            initialFit = false;
        }
    }
    public void Fit()
    {
        if (target == null) return;
        var size = MaximumSize(); Scale = size.X > 0 && size.Y > 0 ? Math.Min(target.Width / size.X, target.Height / size.Y) : 1;
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
    public void PreviousFrame() { if (FrameCount > 0) CurrentFrame = (frame + FrameCount - 1) % FrameCount; }
    public void NextFrame() { if (FrameCount > 0) CurrentFrame = (frame + 1) % FrameCount; }
    public void CopyViewFrom(SpriteSheetAssetPreview previous)
    { spriteTextures = previous.spriteTextures; CurrentFrame = previous.frame; Scale = previous.Scale; Offsets = previous.Offsets; initialFit = false; dirty = true; }
    public void Render()
    {
        if (!Visible || !dirty || target == null || disposed) return;
        var command = game.GraphicsContext.CommandList; var previousSpace = game.GraphicsDevice.ColorSpace;
        try
        {
            game.GraphicsDevice.ColorSpace = colorSpace;
            command.ResourceBarrierTransition(target, BarrierLayout.RenderTarget); command.SetRenderTargetAndViewport(null, target);
            command.Clear(target, new Color4(.2f, .2f, .2f, 1).ToColorSpace(colorSpace));
            if (FrameCount > 0)
            {
                var sprite = sheet.Sprites[frame]; var texture = spriteTextures ? textures[frame] : sprite.Texture;
                var region = spriteTextures && texture != null ? new RectangleF(0, 0, texture.Width, texture.Height) : sprite.Region;
                var orientation = spriteTextures ? ImageOrientation.AsIs : sprite.Orientation;
                batch.Begin(game.GraphicsContext, SpriteSortMode.Texture, blend);
                try { SpriteSheetPreviewRenderer.DrawFrame(batch, texture, region, orientation, new(target.Width, target.Height), Offsets, Scale); }
                finally { batch.End(); }
            }
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
        if (!batchReleased) { batch?.Dispose(); batchReleased = true; }
        if (!sheetReleased)
        {
            if (cache != null) { if (sheet != null) cache.Unload(sheet); else cache.UnloadAll(); }
            sheetReleased = true;
        }
        ReleaseTarget();
        if (contentLease != null) { contentLease.Dispose(); contentLease = null; }
        disposed = true;
    }
    private bool batchReleased, sheetReleased;
}
