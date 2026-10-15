using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal static class SpriteSheetAssetThumbnail
{
    public static (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer, SpriteBatch batch, AssetThumbnailSource source)
    {
        SpriteSheet? sheet = null; Texture? target = null;
        var resources = new ThumbnailGpuResources(game, source.RequireCompiledContent());
        try
        {
            var cache = source.RequireCompiledContent().CreateObjectCache(); resources.Retain(cache.UnloadAll);
            sheet = cache.Load<SpriteSheet>(source.Url, ContentManagerLoaderSettings.StreamingDisabled);
            var format = source.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
            target = Texture.New2D(game.GraphicsDevice, 128, 128, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            resources.Retain(target);
            var command = game.GraphicsContext.CommandList; command.SetRenderTargetAndViewport(null, target);
            command.Clear(target, Color.FromBgra(0xFF434343).ToColor4().ToColorSpace(source.ColorSpace));
            batch.Begin(game.GraphicsContext, SpriteSortMode.Deferred);
            try { SpriteSheetPreviewRenderer.DrawThumbnail(batch, sheet, new Vector2(128)); }
            finally { batch.End(); }
            var captured = AssetThumbnailImage.Capture(game, renderer, target);
            if (captured.Thumbnail.Owns(target)) { resources.Transfer(target); target = null; }
            return captured;
        }
        finally { resources.Retire(); }
    }
}
