using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool ContainsSceneReference(Type type, object? value)
    {
        if (!SceneObjectReference.Accepts(type, value)) return false;
        if (value == null) return true;
        var entity = value is Entity target ? target : (value as EntityComponent)?.Entity;
        return entity != null && Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) && ReferenceEquals(part.Entity, entity)
            && (value is not EntityComponent component || entity.Components.Contains(component));
    }
}
