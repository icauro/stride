using Stride.Core.Extensions;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal static class SceneObjectReference
{
    internal static bool SupportsComponent(Type type) => typeof(EntityComponent).IsAssignableFrom(type)
        || type.IsInterface && type.IsImplementedOnAny<EntityComponent>();
    internal static bool Supports(Type type) => typeof(Entity).IsAssignableFrom(type) || SupportsComponent(type);
    internal static bool AcceptsEntity(Type type, Entity entity) => typeof(Entity).IsAssignableFrom(type) && type.IsInstanceOfType(entity);
    internal static bool Accepts(Type type, object? value) => value == null || type.IsInstanceOfType(value)
        && (value is EntityComponent && SupportsComponent(type) || value is Entity && typeof(Entity).IsAssignableFrom(type));
}
