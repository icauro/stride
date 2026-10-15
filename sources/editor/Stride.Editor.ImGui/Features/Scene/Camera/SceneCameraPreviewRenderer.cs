using Stride.Assets.SpriteFont;
using Stride.Core;
using Stride.Assets.SpriteFont.Compiler;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

/// <summary>WPF selected-camera inset using a second real view of the existing scene/compositor, then GPU composition.</summary>
internal sealed class SceneCameraPreviewRenderer(Game game, ISceneRenderer content, Func<CameraComponent?> selectedCamera,
    Func<bool> enabled, Func<float, float> gameAspect, Action<Exception> failed) : SceneRendererBase
{
    private readonly RenderView view = new();
    private CameraComponent? camera;
    private Viewport inset;
    private bool active;
    private PreviewBatch? batch;
    private SpriteFont? font;
    internal Exception? LastError { get; private set; }
    internal bool Active => active;
    internal string? CameraName => camera?.Entity?.Name;
    internal Viewport Inset => inset;
    protected override void InitializeCore()
    {
        base.InitializeCore();
        batch = new PreviewBatch(game.GraphicsDevice);
        var asset = OfflineRasterizedSpriteFontFactory.Create(); asset.FontType.Size = 8;
        font = OfflineRasterizedFontCompiler.Compile(game.Font, asset, game.GraphicsDevice.ColorSpace == ColorSpace.Linear);
    }
    protected override void CollectCore(RenderContext context)
    {
        base.CollectCore(context);
        var next = selectedCamera();
        if (!ReferenceEquals(camera, next)) LastError = null;
        camera = next; active = enabled() && camera != null && LastError == null;
        if (!active) return;
        var width = context.ViewportState.Viewport0.Width * .3f; var height = context.ViewportState.Viewport0.Height * .2f;
        var aspect = camera!.UseCustomAspectRatio ? camera.AspectRatio : gameAspect(width / height);
        if (!float.IsFinite(aspect) || aspect <= 0) { active = false; return; }
        if (width > height * aspect) width = height * aspect; else height = width / aspect;
        if (width < 32 || height < 32) { active = false; return; }
        inset = new Viewport(0, 0, (int)width, (int)height);
        context.RenderSystem.Views.Add(view);
        try
        {
            using (context.SaveViewportAndRestore())
            {
                context.ViewportState = new ViewportState { Viewport0 = inset };
                SceneCameraRenderer.UpdateCameraToRenderView(context, view, camera);
                using (context.PushRenderViewAndRestore(view))
                using (context.PushTagAndRestore(CameraComponentRendererExtensions.Current, camera)) content.Collect(context);
            }
        }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); context.RenderSystem.Views.Remove(view); LastError = error; active = false; failed(error); }
    }
    protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
    {
        if (!active || camera == null) return;
        Texture? color = null, depth = null;
        var command = drawContext.CommandList; var outputViewport = command.Viewport;
        try
        {
            color = drawContext.GraphicsContext.Allocator.GetTemporaryTexture2D(TextureDescription.New2D((int)inset.Width, (int)inset.Height, 1,
                command.RenderTarget.Format, TextureFlags.RenderTarget | TextureFlags.ShaderResource));
            if (command.DepthStencilBuffer != null)
            {
                var flags = TextureFlags.DepthStencil;
                if (context.GraphicsDevice.Features.HasDepthAsSRV) flags |= TextureFlags.ShaderResource;
                depth = drawContext.GraphicsContext.Allocator.GetTemporaryTexture2D(TextureDescription.New2D((int)inset.Width, (int)inset.Height, 1, command.DepthStencilBuffer.Format, flags));
            }
            using (context.PushRenderViewAndRestore(view))
            using (drawContext.PushTagAndRestore(CameraComponentRendererExtensions.Current, camera))
            using (drawContext.PushRenderTargetsAndRestore())
            {
                command.ResourceBarrierTransition(color, BarrierLayout.RenderTarget);
                command.SetRenderTarget(depth, color); command.SetViewport(inset);
                try { content.Draw(drawContext); }
                finally { command.SetViewport(outputViewport); }
            }
            command.ResourceBarrierTransition(color, BarrierLayout.ShaderResource);
            var position = new Vector2(outputViewport.Width - inset.Width - 16, outputViewport.Height - inset.Height - 16);
            batch!.Begin(drawContext.GraphicsContext, blendState: BlendStates.Default, depthStencilState: DepthStencilStates.None);
            batch.Draw(color, position);
            var white = context.GraphicsDevice.GetSharedWhiteTexture();
            batch.Draw(white, new RectangleF(position.X - 1, position.Y - 1, inset.Width + 2, 1), Color.White);
            batch.Draw(white, new RectangleF(position.X - 1, position.Y + inset.Height, inset.Width + 2, 1), Color.White);
            batch.Draw(white, new RectangleF(position.X - 1, position.Y, 1, inset.Height), Color.White);
            batch.Draw(white, new RectangleF(position.X + inset.Width, position.Y, 1, inset.Height), Color.White);
            batch.End();
            if (font != null && camera.Entity?.Name != null)
            {
                batch.Begin(drawContext.GraphicsContext, blendState: BlendStates.AlphaBlend, depthStencilState: DepthStencilStates.None);
                batch.DrawString(font, camera.Entity.Name, new Vector2(position.X, position.Y - 16), Color.White); batch.End();
            }
        }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); LastError = error; active = false; failed(error); }
        finally
        {
            command.SetViewport(outputViewport);
            if (depth != null) drawContext.GraphicsContext.Allocator.ReleaseReference(depth);
            if (color != null) drawContext.GraphicsContext.Allocator.ReleaseReference(color);
        }
    }
    protected override void Destroy()
    {
        batch?.Dispose(); batch = null; font?.Dispose(); font = null; base.Destroy();
    }
    private sealed class PreviewBatch(GraphicsDevice device) : SpriteBatch(device)
    {
        protected override void Destroy()
        {
            DefaultEffect.Effect.Dispose(); DefaultEffect.Dispose(); DefaultEffectSRgb.Effect.Dispose(); DefaultEffectSRgb.Dispose();
            TextureSpriteFontEffect.Effect.Dispose(); TextureSpriteFontEffect.Dispose(); base.Destroy();
        }
    }
}
