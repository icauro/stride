using Stride.Engine;
using Stride.Particles.Components;
using Stride.Particles.Materials;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private static EntityComponent? FindObservedRuntimeComponent(Entity entity, Guid identity)
    {
        foreach (var component in entity.Components)
            if (component.Id == identity) return component;
        return null;
    }
    private static void NotifyRuntimeComponentPropertyChanged(ComponentField field, EntityComponent component)
    {
        if (component is not ParticleSystemComponent) return;
        if (field.Name is nameof(ParticleMaterialSimple.AlphaAdditive)
            or nameof(ParticleMaterialSimple.ZOffset) or nameof(ParticleMaterialSimple.SoftEdgeDistance))
            (field.ReadRuntimeOwner(component) as ParticleMaterialSimple)?.ForceUpdate();
    }
}
