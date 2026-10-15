using System.Reflection;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

/// <summary>Registers the host's built-in modules before Game discovers declared systems.</summary>
internal static class EditorBuiltInModules
{
    internal static void Register()
    {
        Assembly[] modules =
        [
            typeof(Stride.Engine.Game).Assembly,
            typeof(Stride.UI.UIElement).Assembly,
            typeof(Stride.Audio.AudioEngine).Assembly,
            typeof(Stride.Video.VideoSystem).Assembly,
            typeof(Stride.Engine.PhysicsComponent).Assembly,
            typeof(Stride.Particles.Components.ParticleSystemComponent).Assembly,
            typeof(Stride.Navigation.NavigationMesh).Assembly,
            typeof(Stride.Rendering.Voxels.VoxelVolumeComponent).Assembly,
            typeof(Stride.SpriteStudio.Runtime.SpriteStudioSheet).Assembly,
            typeof(Stride.Assets.Entities.SceneAsset).Assembly,
            typeof(Stride.Assets.Models.ProceduralModelAsset).Assembly,
            typeof(Stride.UI.Assets.UILibraryAsset).Assembly,
            typeof(Stride.Audio.Assets.SoundAsset).Assembly,
            typeof(Stride.Video.Assets.VideoAsset).Assembly,
            typeof(Stride.Physics.Assets.ColliderShapeAsset).Assembly,
            typeof(Stride.SpriteStudio.Assets.SpriteStudioModelAsset).Assembly,
        ];
        foreach (var assembly in modules.Distinct())
        {
            ModuleRuntimeHelpers.RunModuleConstructor(assembly.ManifestModule);
            AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
        }
    }
}
