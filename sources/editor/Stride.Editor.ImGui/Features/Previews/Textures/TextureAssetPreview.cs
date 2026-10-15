using Hexa.NET.ImGui;
using Stride.Assets.Textures;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

/// <summary>ImGui lifecycle/input adapter for the same renderer used by WPF texture previews.</summary>
internal sealed class TextureAssetPreview : IDisposable, IRuntimeTextureConsumer
{
    private readonly Game game;
    private readonly ImGuiRenderer uiRenderer;
    private readonly ContentManager? content;
    private readonly Texture texture;
    private readonly TextureAsset asset;
    private readonly TexturePreviewRenderer engineRenderer;
    private Texture? target;
    private Texture? presentation;
    private Exception? resizeError;
    private (int Width, int Height) failedResize;
    private bool dirty = true;
    private bool initialScale = true;
    private bool rendererReleased, textureReleased;
    private int mip, cubeMode = 6;
    private float slice;
    private float checkerboardPixelScale = 1;
    private static readonly float[] ZoomFactors = [0.02f, 0.05f, 0.083f, 0.125f, 0.167f, 0.2f, 0.25f, 0.333f, 0.5f, 0.667f, 1, 1.5f, 2, 3, 4, 5, 6, 8, 12, 16, 24];
    public bool UsesRuntimeTexture(Texture value) => !IsDisposed && (!textureReleased && ReferenceEquals(texture, value)
        || CompiledTextureConsumers.UsesTexture(content, value));
    public string Name { get; }
    public ImTextureRef TextureRef { get; private set; }
    public int Width => texture.Width;
    public int Height => texture.Height;
    public int Depth => texture.Depth;
    public TextureDimension Dimension => texture.ViewDimension;
    public PixelFormat Format => texture.Format;
    public int MipCount => texture.MipLevelCount;
    public int TargetWidth => target?.Width ?? 0;
    public int TargetHeight => target?.Height ?? 0;
    public float Scale { get; private set; } = 1;
    public Vector2 Offsets { get; private set; }
    public int RenderCount { get; private set; }
    public int DrawCalls { get; private set; }
    public bool Visible { get; set; }
    public bool IsDisposed { get; private set; }
    public ColorSpace PreviewColorSpace { get; }
    public Exception? RenderError { get; private set; }
    internal float CheckerboardPixelScale
    {
        get => checkerboardPixelScale;
        set
        {
            if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (checkerboardPixelScale == value) return;
            checkerboardPixelScale = value; dirty = true;
        }
    }
    public int Mip { get => mip; set { mip = Math.Clamp(value, 0, MipCount - 1); engineRenderer.SetMipLevel(mip); dirty = true; } }
    public int CubeMode { get => cubeMode; set { cubeMode = Math.Clamp(value, 0, 6); dirty = true; } }
    public float Slice { get => slice; set { slice = Math.Clamp(value, 0, Depth); dirty = true; } }

    public void CopyViewFrom(TextureAssetPreview previous)
    {
        Scale = previous.Scale; Offsets = previous.Offsets;
        Mip = previous.mip; CubeMode = previous.cubeMode; Slice = previous.slice;
        initialScale = false; dirty = true;
    }

    public void SetView(float scale, Vector2 offsets)
    {
        if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(offsets.X) || !float.IsFinite(offsets.Y))
            throw new ArgumentOutOfRangeException(nameof(scale));
        Scale = scale; Offsets = offsets; initialScale = false; dirty = true;
    }

    public TextureAssetPreview(Game game, ImGuiRenderer renderer, CompiledProjectContent project, string url, TextureAsset asset,
        ColorSpace projectColorSpace = ColorSpace.Linear)
    {
        this.game = game; uiRenderer = renderer; this.asset = asset; Name = url;
        // Match TexturePreview.DetermineColorSpace, including data/normal-map textures.
        PreviewColorSpace = asset.Type.IsSRgb(projectColorSpace) ? ColorSpace.Linear : ColorSpace.Gamma;
        content = project.CreateObjectCache();
        texture = content.Load<Texture>(url, ContentManagerLoaderSettings.StreamingDisabled);
        TexturePreviewRenderer? created = null;
        try
        {
            created = new TexturePreviewRenderer(game.GraphicsDevice, game.Services);
            engineRenderer = created;
            engineRenderer.SetTexture(texture);
            if (Dimension is not (TextureDimension.Texture2D or TextureDimension.Texture3D or TextureDimension.TextureCube))
                throw new NotSupportedException($"Texture dimension {Dimension} cannot be previewed.");
        }
        catch { created?.Dispose(); content.Unload(texture); throw; }
    }
    public TextureAssetPreview(Game game, ImGuiRenderer renderer, Image source, string name)
    {
        this.game = game; uiRenderer = renderer; Name = name;
        asset = new TextureAsset { Type = new ColorTextureType { UseSRgbSampling = false, PremultiplyAlpha = false } };
        PreviewColorSpace = ColorSpace.Gamma;
        texture = Texture.New(game.GraphicsDevice, source);
        TexturePreviewRenderer? created = null;
        try
        {
            created = new TexturePreviewRenderer(game.GraphicsDevice, game.Services);
            engineRenderer = created; engineRenderer.SetTexture(texture);
            if (Dimension != TextureDimension.Texture2D) throw new NotSupportedException("Sprite source images must be two dimensional.");
        }
        catch { created?.Dispose(); texture.Dispose(); throw; }
    }
    // Private verification supplies an actual compiled-format GPU texture; ownership
    // remains identical to the source-image adapter (Dispose releases the texture).
    internal TextureAssetPreview(Game game, ImGuiRenderer renderer, Texture source, TextureAsset asset, ColorSpace packageColorSpace)
    {
        this.game = game; uiRenderer = renderer; this.asset = asset; texture = source; Name = "texture-parity";
        PreviewColorSpace = asset.Type.IsSRgb(packageColorSpace) ? ColorSpace.Linear : ColorSpace.Gamma;
        engineRenderer = new TexturePreviewRenderer(game.GraphicsDevice, game.Services);
        try { engineRenderer.SetTexture(texture); }
        catch { engineRenderer.Dispose(); throw; }
    }
    internal byte[] ReadParityPixels()
    {
        if (target == null || RenderError != null || dirty || RenderCount == 0)
            throw new InvalidOperationException("Texture parity requires a successfully rendered actual target.");
        return target.GetData<byte>(game.GraphicsContext.CommandList);
    }
    internal PixelFormat ParityTargetFormat => target?.Format ?? PixelFormat.None;
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        width = Math.Clamp(width, 1, 4096); height = Math.Clamp(height, 1, 4096);
        if (target?.Width == width && target.Height == height) return;
        if (resizeError != null && failedResize == (width, height)) throw resizeError;
        var format = PreviewColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var displayFormat = game.GraphicsDevice.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var next = new PreviewTargetResources(game, uiRenderer);
        try
        {
            next.Allocate(width, height, format, displayFormat);
            ReleaseTarget();
        }
        catch (Exception error)
        {
            next.Retire();
            EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error);
            resizeError = error; failedResize = (width, height); throw;
        }
        targetResources = next; target = next.Target; presentation = next.Presentation; TextureRef = next.Reference;
        RenderError = null;
        resizeError = null;
        dirty = true;
        if (initialScale) { Fit(); Scale = Math.Min(1, Scale); initialScale = false; }
    }
    public void Fit(float margin = 0)
    {
        if (target == null) return;
        var size = engineRenderer.GetSpriteSize(cubeMode);
        Scale = Math.Max(.001f, Math.Min((target.Width - margin) / size.X, (target.Height - margin) / size.Y)); Offsets = Vector2.Zero; dirty = true;
    }
    public void ActualSize() { Scale = 1; dirty = true; }
    public void Zoom(bool zoomIn, Vector2? pointer = null)
    {
        var next = zoomIn ? ZoomFactors.FirstOrDefault(value => value > Scale, ZoomFactors[^1]) : ZoomFactors.LastOrDefault(value => value < Scale, ZoomFactors[0]);
        if (pointer != null && target != null)
            Offsets += (pointer.Value - new Vector2(target.Width, target.Height) / 2) * (1 / next - 1 / Scale);
        Scale = next; dirty = true;
    }
    public void Pan(Vector2 pixels) { Offsets += pixels / Scale; dirty = true; }
    public void SetOffsets(Vector2 offsets) { if (Offsets != offsets) { Offsets = offsets; dirty = true; } }
    public void Focus(Vector2 sourcePoint) { Offsets = new Vector2(Width, Height) / 2 - sourcePoint; dirty = true; }
    public void PreviousSlice() => Slice = (float)(Math.Ceiling(Slice + Depth - 1) % Depth);
    public void NextSlice() => Slice = (float)(Math.Floor(Slice + 1) % Depth);
    public void Render()
    {
        DrawCalls = 0;
        if (!Visible || !dirty || target == null) return;
        var before = game.GraphicsDevice.FrameDrawCalls;
        var command = game.GraphicsContext.CommandList;
        try
        {
            command.ResourceBarrierTransition(target, BarrierLayout.RenderTarget);
            command.SetRenderTargetAndViewport(null, target);
            // Same LDR background as EditorServiceGame.EditorBackgroundColorLdr.
            command.Clear(target, new Color4(0.2f, 0.2f, 0.2f, 1).ToColorSpace(PreviewColorSpace));
            if (content == null) engineRenderer.RenderCheckerboard(game.GraphicsContext, new Vector2(target.Width, target.Height), Scale, Offsets, checkerboardPixelScale);
            var swizzle = asset.Type.Hint == TextureHint.Grayscale ? SwizzleMode.RRR1 : asset.Type.Hint == TextureHint.NormalMap ? SwizzleMode.NormalMap : SwizzleMode.None;
            engineRenderer.Render(game.GraphicsContext, new Vector2(target.Width, target.Height), Scale, Offsets, asset.Type.PremultiplyAlpha, swizzle, cubeMode, slice);
            if (presentation != null)
            {
                // Preserve the WPF preview's encoded pixels and alpha blending. A raw GPU copy
                // changes the sampling format for the host without another gamma conversion.
                command.ResourceBarrierTransition(target, BarrierLayout.CopySource);
                command.ResourceBarrierTransition(presentation, BarrierLayout.CopyDest);
                command.Copy(target, presentation);
                command.ResourceBarrierTransition(presentation, BarrierLayout.ShaderResource);
            }
            else command.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
            DrawCalls = checked((int)(game.GraphicsDevice.FrameDrawCalls - before));
            RenderError = null; dirty = false; RenderCount++;
        }
        catch (Exception error)
        {
            EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error);
            RenderError = error; dirty = false;
        }
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        Visible = false;
        if (!rendererReleased) { engineRenderer.Dispose(); rendererReleased = true; }
        if (!textureReleased) { if (content != null) content.Unload(texture); else texture.Dispose(); textureReleased = true; }
        ReleaseTarget();
        IsDisposed = true;
    }
    private void ReleaseTarget()
    {
        if (targetResources == null) return;
        targetResources.Retire(); targetResources = null;
        target = presentation = null; TextureRef = default;
    }
    private PreviewTargetResources? targetResources;
}
