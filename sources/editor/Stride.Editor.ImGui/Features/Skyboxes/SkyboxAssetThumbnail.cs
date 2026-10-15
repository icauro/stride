using System.Security.Cryptography;
using Stride.Assets;
using Stride.Assets.Skyboxes;
using Stride.Engine;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal static class SkyboxAssetThumbnail
{
    private static byte[]? pixels;
    private static byte[] Pixels => pixels ??= File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Thumbnails", "SkyboxThumbnail.png"));
    private static string? identity;
    private static string Identity => identity ??= "wpf-static-skybox/" + Convert.ToHexString(SHA256.HashData(Pixels));
    internal static AssetThumbnailSource CreateSource(CompiledProjectContent content, SkyboxAsset asset, string url)
        => new(content, asset, url, ColorSpace.Gamma, Identity, "static", RenderingMode.LDR, [], Identity);
    internal static (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer)
        => (AssetThumbnailImage.LoadCached(game, renderer, Pixels), Pixels);
}
