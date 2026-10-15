using Stride.Assets.SpriteFont;
using Stride.Assets.SpriteFont.Compiler;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Core;
using Stride.Core.Reflection;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.Font;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

/// <summary>WPF's registered Asset fallback: original artwork, type color and real font glyphs.</summary>
internal sealed class CustomAssetThumbnail : IDisposable
{
    private SpriteFont? font;
    private Texture? background;
    private ColorSpace backgroundSpace;
    private ThumbnailGpuResources? sharedResources;
    public static string Identity(Type type)
    {
        var title = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(type)?.Name ?? type.Name;
        var color = (Color)type.GetUniqueColor();
        // WPF's unique color includes the process's string hash; do not reuse a different tint from disk.
        return $"custom-wpf-v1:{type.AssemblyQualifiedName}:{title}:{color.ToRgba()}";
    }
    public (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer,
        SpriteBatch batch, AssetThumbnailSource source)
    {
        sharedResources ??= new ThumbnailGpuResources(game);
        if (font == null)
        {
            var settings = OfflineRasterizedSpriteFontFactory.Create();
            settings.FontType.Size = 22;
            font = OfflineRasterizedFontCompiler.Compile(game.Services.GetSafeServiceAs<IFontFactory>(), settings, true);
            sharedResources.Retain(font);
        }
        var previousSpace = game.GraphicsDevice.ColorSpace;
        Texture? target = null;
        var resources = new ThumbnailGpuResources(game);
        try
        {
            if (background != null && backgroundSpace != source.ColorSpace)
            {
                var previous = background; var retired = new ThumbnailGpuResources(game);
                retired.Retain(previous); retired.Retire(); sharedResources.Transfer(previous); background = null;
            }
            if (background == null)
            {
                using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Icons", "thumbnail-user-asset.png"));
                background = Texture.Load(game.GraphicsDevice, stream, loadAsSrgb: source.ColorSpace == ColorSpace.Linear);
                sharedResources.Retain(background);
                backgroundSpace = source.ColorSpace;
            }
            game.GraphicsDevice.ColorSpace = source.ColorSpace;
            var format = source.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
            target = Texture.New2D(game.GraphicsDevice, 128, 128, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            resources.Retain(target);
            var command = game.GraphicsContext.CommandList;
            command.SetRenderTargetAndViewport(null, target);
            command.Clear(target, Color.FromBgra(0xFF434343).ToColor4().ToColorSpace(source.ColorSpace));
            var type = source.Asset.GetType();
            var title = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(type)?.Name ?? type.Name;
            batch.Begin(game.GraphicsContext);
            try
            {
                TextureThumbnailRenderer.DrawBackground(batch, background, new Vector2(128), (Color)type.GetUniqueColor());
                SpriteFontPreviewRenderer.DrawThumbnail(batch, font, title, new Vector2(128), Color.White, .95f);
            }
            finally { batch.End(); }
            game.GraphicsDevice.ColorSpace = previousSpace;
            var captured = AssetThumbnailImage.Capture(game, renderer, target);
            if (captured.Thumbnail.Owns(target)) { resources.Transfer(target); target = null; }
            return captured;
        }
        finally { game.GraphicsDevice.ColorSpace = previousSpace; resources.Retire(); }
    }
    public void Dispose()
    { sharedResources?.Retire(); sharedResources = null; background = null; font = null; }
}
