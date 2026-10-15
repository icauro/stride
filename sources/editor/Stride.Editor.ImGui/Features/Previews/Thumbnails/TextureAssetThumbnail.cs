using Stride.Assets.Textures;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

/// <summary>Hosts the shared WPF compiled-texture thumbnail background pass.</summary>
internal static class TextureAssetThumbnail
{
    public static (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer,
        SpriteBatch batch, CompiledProjectContent content, string url, TextureAsset asset, ColorSpace projectColorSpace)
    {
        var resources = new ThumbnailGpuResources(game, content);
        Texture? source = null, face = null, target = null;
        try
        {
            var cache = content.CreateObjectCache(); resources.Retain(cache.UnloadAll);
            source = cache.Load<Texture>(url, ContentManagerLoaderSettings.StreamingDisabled);
            if (source.ViewDimension == TextureDimension.TextureCube)
            {
                face = source.ToTextureView(new TextureViewDescription { ArraySlice = 0, Type = ViewType.ArrayBand });
                resources.Retain(face);
            }
            else if (source.ViewDimension != TextureDimension.Texture2D)
                throw new NotSupportedException("The reference texture thumbnail compiler supports 2D textures and cube face 0.");
            var space = asset.Type.IsSRgb(projectColorSpace) ? ColorSpace.Linear : ColorSpace.Gamma;
            var format = space == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
            target = Texture.New2D(game.GraphicsDevice, 128, 128, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            resources.Retain(target);
            var command = game.GraphicsContext.CommandList;
            command.SetRenderTargetAndViewport(null, target);
            command.Clear(target, Color.FromBgra(0xFF434343).ToColor4().ToColorSpace(space));
            var swizzle = asset.Type.Hint == TextureHint.Grayscale ? SwizzleMode.RRR1
                : asset.Type.Hint == TextureHint.NormalMap ? SwizzleMode.NormalMap : SwizzleMode.None;
            batch.Begin(game.GraphicsContext, SpriteSortMode.Deferred);
            try { TextureThumbnailRenderer.DrawBackground(batch, face ?? source, new Vector2(128), Color.White, Color.Zero, swizzle); }
            finally { batch.End(); }
            var captured = AssetThumbnailImage.Capture(game, renderer, target);
            if (captured.Thumbnail.Owns(target)) { resources.Transfer(target); target = null; }
            return captured;
        }
        finally
        {
            resources.Retire();
        }
    }
}
