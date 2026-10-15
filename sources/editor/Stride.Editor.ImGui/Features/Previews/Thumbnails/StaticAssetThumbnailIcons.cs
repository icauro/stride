using Stride.Video.Assets;
using Stride.Audio.Assets;
using Stride.Assets;
using Stride.Assets.Entities;
using Stride.Assets.Rendering;
using Stride.Assets.Scripts;
using Stride.Assets.Models;
using Stride.Assets.Media;
using Stride.UI.Assets;
using Stride.Assets.Skyboxes;
using Stride.SpriteStudio.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Original artwork used by WPF's registered static thumbnail compilers.</summary>
internal static class StaticAssetThumbnailIcons
{
    internal static readonly Type[] AssetTypes = [typeof(GameSettingsAsset), typeof(SkyboxAsset), typeof(GraphicsCompositorAsset),
        typeof(SceneAsset), typeof(ScriptSourceFileAsset), typeof(SoundAsset), typeof(VideoAsset), typeof(SkeletonAsset),
        typeof(SpriteStudioAnimationAsset), typeof(UILibraryAsset)];
    public static string? ForAssetType(Type assetType)
    {
        if (assetType == typeof(GameSettingsAsset)) return "thumbnail-game-settings";
        if (assetType == typeof(SkyboxAsset)) return "thumbnail-skybox";
        if (assetType == typeof(GraphicsCompositorAsset)) return "thumbnail-graphics-compositor";
        if (assetType == typeof(SceneAsset)) return "thumbnail-scene";
        if (assetType == typeof(ScriptSourceFileAsset)) return "thumbnail-script-source";
        if (assetType == typeof(SoundAsset)) return "thumbnail-sound";
        if (assetType == typeof(VideoAsset)) return "thumbnail-video";
        if (assetType == typeof(SkeletonAsset)) return "thumbnail-skeleton";
        if (assetType == typeof(SpriteStudioAnimationAsset)) return "thumbnail-animation";
        if (assetType == typeof(UILibraryAsset)) return "thumbnail-ui-library";
        return null;
    }
}
