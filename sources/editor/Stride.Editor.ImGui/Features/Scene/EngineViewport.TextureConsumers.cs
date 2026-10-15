using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal bool UsesRuntimeTexture(Texture texture, EntityHierarchyDocument? document = null)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        bool Uses(object? value) => CompiledTextureConsumers.UsesTexture(value, texture, visited);
        if (Uses(loadedScene) || Uses(content) || componentContent.Values.Any(Uses)) return true;
        if (ImportRuntimeEntities(document ?? PresentedDocument).Any(Uses)) return true;
        foreach (var owner in ImportRuntimeOwners(document ?? PresentedDocument))
            foreach (var field in owner.Owner.ObservedComponentFields)
            {
                if (!field.IsContentReference || !owner.Entities.TryGetValue(field.Component.Entity.Id, out var entity)) continue;
                var component = entity.Components.FirstOrDefault(candidate => candidate.Id == field.Component.Id);
                if (component != null && Uses(field.ReadRuntime(component))) return true;
            }
        return false;
    }
}
