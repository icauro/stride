using Stride.Core.Assets;
using Stride.Assets.Models;

namespace Stride.Assets.Thumbnails;

/// <summary>Native asset rules shared by the production thumbnails and editor previews.</summary>
public static class AssetThumbnailRules
{
    public const float UIDesignDensity = 100.0f;

    /// <summary>Load the production registrations before constructing a compiler registry.</summary>
    public static void EnsureRegistered()
        => System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Module).Module.ModuleHandle);

    public static AssetItem FindAnimationPreviewModel(AssetItem assetItem)
    {
        var animationAsset = (AnimationAsset)assetItem.Asset;
        var previewModelAsset = animationAsset?.PreviewModel != null
            ? assetItem.Package.FindAssetFromProxyObject(animationAsset.PreviewModel)
            : null;
        return (previewModelAsset?.Asset as ModelAsset)?.Skeleton != null ? previewModelAsset : null;
    }
}
