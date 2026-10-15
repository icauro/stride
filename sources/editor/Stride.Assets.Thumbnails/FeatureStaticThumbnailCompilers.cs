using System.IO;
using Stride.Audio.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Editor.Thumbnails;
using Stride.UI.Assets;
using Stride.Video.Assets;

namespace Stride.Assets.Thumbnails;

// These package thumbnails are declared by toolkit editor companions upstream.
// Register the same native compiler and artwork without loading their WPF services.
[AssetCompiler(typeof(SoundAsset), typeof(ThumbnailCompilationContext))]
internal sealed class SoundThumbnailCompiler : StaticThumbnailCompiler<SoundAsset>
{
    public SoundThumbnailCompiler() : base(FeatureStaticThumbnailResources.Read("SoundThumbnail.png")) { }
}

[AssetCompiler(typeof(VideoAsset), typeof(ThumbnailCompilationContext))]
internal sealed class VideoThumbnailCompiler : StaticThumbnailCompiler<VideoAsset>
{
    public VideoThumbnailCompiler() : base(FeatureStaticThumbnailResources.Read("VideoThumbnail.png")) { }
}

[AssetCompiler(typeof(UILibraryAsset), typeof(ThumbnailCompilationContext))]
internal sealed class UILibraryThumbnailCompiler : StaticThumbnailCompiler<UILibraryAsset>
{
    public UILibraryThumbnailCompiler() : base(FeatureStaticThumbnailResources.Read("UILibraryThumbnail.png")) { }
}

internal static class FeatureStaticThumbnailResources
{
    internal static byte[] Read(string name)
    {
        using var source = typeof(FeatureStaticThumbnailResources).Assembly.GetManifestResourceStream("Stride.Assets.Thumbnails." + name)
            ?? throw new FileNotFoundException("Native static thumbnail artwork was not embedded.", name);
        using var target = new MemoryStream();
        source.CopyTo(target);
        return target.ToArray();
    }
}
