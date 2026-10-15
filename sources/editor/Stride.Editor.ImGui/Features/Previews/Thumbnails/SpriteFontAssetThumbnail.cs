using Stride.Assets.SpriteFont;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal static class SpriteFontAssetThumbnail
{
    public static string Title(Stride.Core.Assets.Asset asset) => asset switch
    {
        SpriteFontAsset font => SpriteFontPreviewRenderer.SplitFontName(font.FontSource.GetFontName()) + "\n" + font.FontType.Size + " " + font.FontSource.Style,
        PrecompiledSpriteFontAsset font => "Precompiled\n" + SpriteFontPreviewRenderer.SplitFontName(font.FontName ?? font.FontDataFile.GetFileNameWithoutExtension())
            + (string.IsNullOrEmpty(font.FontName) ? "" : "\n" + font.Size + " " + font.Style),
        _ => throw new ArgumentException("The asset is not a sprite font.")
    };
    public static (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer, SpriteBatch batch, AssetThumbnailSource source)
    {
        var resources = new ThumbnailGpuResources(game, source.RequireCompiledContent());
        Texture? target = null;
        var previousSpace = game.GraphicsDevice.ColorSpace;
        try
        {
            var content = new CompiledFontContent(game, source.RequireCompiledContent(), source.Url); resources.Retain(content);
            var font = content.Font;
            content.Draw();
            game.GraphicsDevice.ColorSpace = source.ColorSpace;
            var format = source.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
            target = Texture.New2D(game.GraphicsDevice, 128, 128, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            resources.Retain(target);
            var command = game.GraphicsContext.CommandList; command.SetRenderTargetAndViewport(null, target);
            command.Clear(target, Color.FromBgra(0xFF434343).ToColor4().ToColorSpace(source.ColorSpace));
            batch.Begin(game.GraphicsContext, SpriteSortMode.Deferred, null, null, null, null,
                font.FontType == SpriteFontType.SDF ? batch.TextureSpriteFontEffect : null);
            try { SpriteFontPreviewRenderer.DrawThumbnail(batch, font, Title(source.Asset), new Vector2(128), Color.White); }
            finally { batch.End(); }
            game.GraphicsDevice.ColorSpace = previousSpace;
            var captured = AssetThumbnailImage.Capture(game, renderer, target);
            if (captured.Thumbnail.Owns(target)) { resources.Transfer(target); target = null; }
            return captured;
        }
        finally { game.GraphicsDevice.ColorSpace = previousSpace; resources.Retire(); }
    }
}
