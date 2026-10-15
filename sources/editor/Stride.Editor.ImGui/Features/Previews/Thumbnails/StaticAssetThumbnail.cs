using System.Security.Cryptography;
using Stride.Engine;
using Stride.Graphics;
using Stride.TextureConverter;

namespace Stride.Editor.ImGui;

/// <summary>Original StaticThumbnailCommand conversion over the original artwork.</summary>
internal static class StaticAssetThumbnail
{
    internal static string Identity(string icon, ColorSpace space)
        => "static-wpf-lanczos3-v1/128/" + icon + "/" + space + "/"
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ImagePath(icon))));

    internal static (AssetThumbnailImage Thumbnail, byte[] Png) Generate(Game game, ImGuiRenderer renderer, AssetThumbnailSource source)
    {
        var icon = source.StaticIcon ?? StaticAssetThumbnailIcons.ForAssetType(source.Asset.GetType())
            ?? throw new InvalidOperationException("No registered static thumbnail artwork.");
        using var stream = File.OpenRead(ImagePath(icon));
        using var image = Image.Load(stream);
        using var tool = new TextureTool();
        using var input = tool.Load(image, source.ColorSpace == ColorSpace.Linear);
        tool.Decompress(input, input.Format.IsSRgb);
        tool.Resize(input, 128, 128, Filter.Rescaling.Lanczos3);
        using var output = tool.ConvertToStrideImage(input);
        using var encoded = new MemoryStream();
        output.Save(encoded, ImageFileType.Png);
        var png = encoded.ToArray();
        return (AssetThumbnailImage.LoadCached(game, renderer, png), png);
    }

    private static string ImagePath(string icon) => Path.Combine(AppContext.BaseDirectory, "Icons", icon + ".png");
}
