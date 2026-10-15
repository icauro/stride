using Stride.Video.Assets;
using Stride.Assets.Models;
using Stride.Assets.Sprite;
using Stride.Assets.Textures;
using Stride.UI.Assets;
using Stride.Assets.Media;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Sprites;
using Stride.SpriteStudio.Assets;
using Stride.SpriteStudio.Runtime;
using Stride.Video;

namespace Stride.Editor.ImGui;

/// <summary>The actual component policies registered by the WPF entity hierarchy editor.</summary>
internal static class AssetEntityFactories
{
    internal static bool Supports(Type type) => typeof(IModelAsset).IsAssignableFrom(type)
        || typeof(SpriteSheetAsset).IsAssignableFrom(type) || typeof(TextureAsset).IsAssignableFrom(type)
        || typeof(UIPageAsset).IsAssignableFrom(type) || typeof(VideoAsset).IsAssignableFrom(type)
        || typeof(SpriteStudioModelAsset).IsAssignableFrom(type);
    internal static void RegisterAssemblies()
    {
        foreach (var assembly in new[] { typeof(VideoComponent).Assembly, typeof(SpriteStudioComponent).Assembly, typeof(SpriteStudioModelAsset).Assembly }.Distinct())
            AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
    }
    internal static bool CanCreate(Asset asset, out string reason)
    {
        reason = "";
        if (asset is SpriteSheetAsset { Type: SpriteSheetType.UI }) { reason = "Sprite sheet type is UI"; return false; }
        if (Supports(asset.GetType())) return true;
        reason = $"Can't add {asset.GetType().Name}s here"; return false;
    }
    internal static Entity Create(AssetItem asset)
    {
        var component = CreateComponent(asset);
        var name = System.IO.Path.GetFileName(asset.UnqualifiedUrl.ToString());
        var entity = new Entity(name); entity.Add(component); return entity;
    }
    internal static Type? ComponentType(Asset asset) => asset switch
    {
        IModelAsset => typeof(ModelComponent),
        SpriteSheetAsset or TextureAsset => typeof(SpriteComponent),
        UIPageAsset => typeof(UIComponent),
        VideoAsset => typeof(VideoComponent),
        SpriteStudioModelAsset => typeof(SpriteStudioComponent),
        _ => null,
    };
    internal static EntityComponent CreateComponent(AssetItem asset)
    {
        if (!CanCreate(asset.Asset, out var reason)) throw new InvalidOperationException(reason);
        T Reference<T>() where T : class => (T)ContentAssetReference.Create(typeof(T), asset);
        return asset.Asset switch
        {
            IModelAsset => new ModelComponent { Model = Reference<Model>() },
            SpriteSheetAsset => new SpriteComponent { SpriteProvider = new SpriteFromSheet { Sheet = Reference<SpriteSheet>() } },
            TextureAsset => new SpriteComponent { SpriteProvider = new SpriteFromTexture { Texture = Reference<Texture>() } },
            UIPageAsset => new UIComponent { Page = Reference<UIPage>() },
            VideoAsset => new VideoComponent { Source = Reference<Stride.Video.Video>() },
            SpriteStudioModelAsset => new SpriteStudioComponent { Sheet = Reference<SpriteStudioSheet>() },
            _ => throw new InvalidOperationException(reason),
        };
    }
}
