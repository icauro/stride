using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Physics;

namespace Stride.Editor.ImGui;

/// <summary>WPF HeightmapThumbnailCommand's actual compiled-height red-channel pass.</summary>
internal static class HeightmapAssetThumbnail
{
    public static (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer,
        SpriteBatch batch, AssetThumbnailSource source)
    {
        var resources = new ThumbnailGpuResources(game, source.RequireCompiledContent());
        Heightmap? heightmap = null; Texture? texture = null, target = null;
        var previousSpace = game.GraphicsDevice.ColorSpace;
        try
        {
            var content = source.RequireCompiledContent().CreateObjectCache(); resources.Retain(content.UnloadAll);
            heightmap = content.Load<Heightmap>(source.Url, ContentManagerLoaderSettings.StreamingDisabled);
            texture = heightmap.CreateTexture(game.GraphicsDevice);
            resources.Retain(texture);
            game.GraphicsDevice.ColorSpace = ColorSpace.Linear;
            target = Texture.New2D(game.GraphicsDevice, 128, 128, PixelFormat.R8G8B8A8_UNorm_SRgb,
                TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            resources.Retain(target);
            var command = game.GraphicsContext.CommandList;
            command.SetRenderTargetAndViewport(null, target);
            command.Clear(target, Color.FromBgra(0xFF434343).ToColor4().ToColorSpace(ColorSpace.Linear));
            batch.Begin(game.GraphicsContext);
            try
            {
                batch.Draw(texture, new RectangleF(0, 0, 128, 128), new RectangleF(0, 0, texture.Width, texture.Height),
                    Color.White, 0, Vector2.Zero, SpriteEffects.None, swizzle: SwizzleMode.RRR1);
            }
            finally { batch.End(); }
            game.GraphicsDevice.ColorSpace = previousSpace;
            var result = AssetThumbnailImage.Capture(game, renderer, target);
            if (result.Thumbnail.Owns(target)) { resources.Transfer(target); target = null; }
            return result;
        }
        finally
        {
            game.GraphicsDevice.ColorSpace = previousSpace;
            resources.Retire();
        }
    }
}
