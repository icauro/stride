using Stride.Core;
using Stride.Core.Reflection;

namespace Stride.Assets.Thumbnails;

internal static class Module
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // These companions can be reused from the host load context before a
        // package container sees them; register the actual native asset modules.
        AssemblyRegistry.Register(typeof(Stride.Audio.Assets.SoundAsset).Assembly, AssemblyCommonCategories.Assets);
        AssemblyRegistry.Register(typeof(Stride.Video.Assets.VideoAsset).Assembly, AssemblyCommonCategories.Assets);
        AssemblyRegistry.Register(typeof(Stride.UI.Assets.UIPageAsset).Assembly, AssemblyCommonCategories.Assets);
        AssemblyRegistry.Register(typeof(Stride.Physics.Assets.HeightmapAsset).Assembly, AssemblyCommonCategories.Assets);
        AssemblyRegistry.Register(typeof(Stride.SpriteStudio.Assets.SpriteStudioModelAsset).Assembly, AssemblyCommonCategories.Assets);
        AssemblyRegistry.Register(typeof(Module).Assembly, AssemblyCommonCategories.Assets);
    }
}
