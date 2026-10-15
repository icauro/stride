using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Processors;
using Stride.Rendering.Lights;
using Stride.Rendering.Compositing;
using Stride.Rendering.Voxels;
using Stride.Rendering.Voxels.VoxelGI;
using Stride.Core.Reflection;
using Stride.Navigation;
using Stride.Particles.Components;

namespace Stride.Editor.ImGui;

internal enum EditorEntityKind { PerspectiveCamera, OrthographicCamera, DirectionalLight, PointLight, SpotLight, AmbientLight, AudioEmitter, Sprite, UI, EmptyParticleSystem, NavigationBoundingBox, AudioListener, Background, SpriteStudio, ModelInstance, SimpleParticleSystem, SmokeParticleSystem, RibbonParticleSystem, VoxelLight, VoxelVolume, LightProbe, SkyboxLight }
internal sealed record EditorEntityFactoryDescriptor(EditorEntityKind Kind, string Category, string Label, Type ComponentType);

internal static class EntityCreationFactories
{
    internal static readonly EditorEntityFactoryDescriptor[] Registered =
    [
        new(EditorEntityKind.PerspectiveCamera, "Camera", "Perspective camera", typeof(CameraComponent)),
        new(EditorEntityKind.OrthographicCamera, "Camera", "Orthographic camera", typeof(CameraComponent)),
        new(EditorEntityKind.DirectionalLight, "Light", "Directional light", typeof(LightComponent)),
        new(EditorEntityKind.PointLight, "Light", "Point light", typeof(LightComponent)),
        new(EditorEntityKind.SpotLight, "Light", "Spot light", typeof(LightComponent)),
        new(EditorEntityKind.AmbientLight, "Light", "Ambient light", typeof(LightComponent)),
        new(EditorEntityKind.SkyboxLight, "Light", "Skybox light", typeof(LightComponent)),
        new(EditorEntityKind.VoxelLight, "Light", "Voxel light", typeof(LightComponent)),
        new(EditorEntityKind.VoxelVolume, "Light", "Voxel volume", typeof(VoxelVolumeComponent)),
        new(EditorEntityKind.LightProbe, "Light", "Light probe", typeof(LightProbeComponent)),
        new(EditorEntityKind.AudioEmitter, "Audio", "Audio emitter", typeof(AudioEmitterComponent)),
        new(EditorEntityKind.AudioListener, "Audio", "Audio listener", typeof(AudioListenerComponent)),
        new(EditorEntityKind.Sprite, "2D", "Sprite", typeof(SpriteComponent)),
        new(EditorEntityKind.UI, "2D", "UI", typeof(UIComponent)),
        new(EditorEntityKind.Background, "2D", "Background", typeof(BackgroundComponent)),
        new(EditorEntityKind.SpriteStudio, "2D", "SpriteStudio", typeof(SpriteStudioComponent)),
        new(EditorEntityKind.ModelInstance, "Model", "Model Instance", typeof(InstanceComponent)),
        new(EditorEntityKind.EmptyParticleSystem, "Particle system", "Empty particle system", typeof(ParticleSystemComponent)),
        new(EditorEntityKind.SimpleParticleSystem, "Particle system", "Simple particle system", typeof(ParticleSystemComponent)),
        new(EditorEntityKind.SmokeParticleSystem, "Particle system", "Fountain particle system", typeof(ParticleSystemComponent)),
        new(EditorEntityKind.RibbonParticleSystem, "Particle system", "Ribbon particle system", typeof(ParticleSystemComponent)),
        new(EditorEntityKind.NavigationBoundingBox, "Navigation", "Bounding box", typeof(NavigationBoundingBoxComponent)),
    ];

    // Native EntityFactoryCategory.CompareTo reverses category priority, then compares names using the current culture.
    internal static readonly string[] Categories = Registered.Select(factory => factory.Category).Distinct()
        .OrderByDescending(category => category switch
        {
            "Model" => 10, "Light" => 20, "Camera" => 30, "2D" => 40,
            "Audio" or "Particle system" => 50, "Navigation" => 60, _ => int.MaxValue,
        }).ThenBy(category => category, StringComparer.CurrentCulture).ToArray();

    internal static void RegisterComponentAssemblies()
    {
        AssetEntityFactories.RegisterAssemblies();
        foreach (var assembly in Registered.Select(factory => factory.ComponentType.Assembly).Distinct())
            AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
    }

    internal static Entity Create(EditorEntityKind kind, SceneCameraSlotId cameraSlot)
    {
        if (kind == EditorEntityKind.SkyboxLight) throw new InvalidOperationException("Skybox light creation requires an asset picker selection.");
        Entity WithComponent(string name, EntityComponent component)
        { var entity = new Entity(name); entity.Add(component); return entity; }
        if (kind == EditorEntityKind.VoxelLight) return WithComponent("Voxel light", new LightComponent { Type = new LightVoxel() });
        if (kind == EditorEntityKind.VoxelVolume) return WithComponent("Voxel volume", new VoxelVolumeComponent { Attributes = { new VoxelAttributeEmissionOpacity() } });
        if (kind == EditorEntityKind.LightProbe) return WithComponent("Light probe", new LightProbeComponent());
        if (kind == EditorEntityKind.AudioEmitter) return WithComponent("AudioEmitter", new AudioEmitterComponent());
        if (kind == EditorEntityKind.AudioListener) return WithComponent("AudioListener", new AudioListenerComponent());
        if (kind == EditorEntityKind.Sprite) return WithComponent("Sprite", new SpriteComponent());
        if (kind == EditorEntityKind.UI) return WithComponent("UI", new UIComponent());
        if (kind == EditorEntityKind.Background) return WithComponent("Background", new BackgroundComponent());
        if (kind == EditorEntityKind.SpriteStudio) return WithComponent("SpriteStudio", new SpriteStudioComponent());
        if (kind == EditorEntityKind.ModelInstance) return WithComponent("Instance", new InstanceComponent());
        if (kind == EditorEntityKind.EmptyParticleSystem) return WithComponent("Empty particle system", new ParticleSystemComponent());
        if (kind == EditorEntityKind.SimpleParticleSystem) return ParticleEntityFactories.Create(EditorParticleEntityKind.Simple);
        if (kind == EditorEntityKind.SmokeParticleSystem) return ParticleEntityFactories.Create(EditorParticleEntityKind.Smoke);
        if (kind == EditorEntityKind.RibbonParticleSystem) return ParticleEntityFactories.Create(EditorParticleEntityKind.Ribbon);
        if (kind == EditorEntityKind.NavigationBoundingBox) return WithComponent("Navigation bounding box", new NavigationBoundingBoxComponent { Size = Vector3.One });
        if (kind is EditorEntityKind.PerspectiveCamera or EditorEntityKind.OrthographicCamera)
        {
            var entity = new Entity("Camera");
            entity.Add(new CameraComponent { Projection = kind == EditorEntityKind.PerspectiveCamera ? CameraProjectionMode.Perspective : CameraProjectionMode.Orthographic,
                Slot = kind == EditorEntityKind.PerspectiveCamera ? cameraSlot : default });
            return entity;
        }
        var light = new Entity(kind switch { EditorEntityKind.PointLight => "Point light", EditorEntityKind.SpotLight => "Spot light",
            EditorEntityKind.AmbientLight => "Ambient light", _ => "Directional light" });
        var component = new LightComponent();
        if (kind == EditorEntityKind.PointLight) component.Type = new LightPoint();
        if (kind == EditorEntityKind.SpotLight) component.Type = new LightSpot();
        if (kind == EditorEntityKind.AmbientLight) component.Type = new LightAmbient();
        light.Add(component);
        if (kind != EditorEntityKind.AmbientLight) light.Transform.Position = new Vector3(0, 2, 0);
        if (kind == EditorEntityKind.DirectionalLight) light.Transform.Rotation = Quaternion.RotationX(MathUtil.DegreesToRadians(-70));
        if (kind == EditorEntityKind.SpotLight) light.Transform.Rotation = Quaternion.RotationX(MathUtil.DegreesToRadians(-90));
        return light;
    }
}
