using Stride.Engine;
using System.Runtime.CompilerServices;

namespace Stride.Editor.ImGui;

internal sealed class ScenePickingOptions
{
    private readonly HashSet<string> excludedTypes = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Type, TypeEligibility> typeEligibility = new();
    private sealed class TypeEligibility { internal long Version = -1; internal bool Pickable; }
    internal long Version { get; private set; }
    internal Func<Entity, bool> Predicate { get; }
    internal ScenePickingOptions() { Predicate = CanPick; }
    private static string Key(Type type) => type.FullName + "," + type.Assembly.GetName().Name;
    internal bool IsExcluded(Type type) => excludedTypes.Contains(Key(type));
    internal void SetExcluded(Type type, bool excluded)
    {
        if (excluded ? excludedTypes.Add(Key(type)) : excludedTypes.Remove(Key(type))) Version++;
    }
    private bool CanPick(Entity entity)
    {
        if (excludedTypes.Count == 0) return true;
        foreach (var component in entity.Components)
        {
            var componentType = component.GetType();
            var cached = typeEligibility.GetValue(componentType, static _ => new TypeEligibility());
            if (cached.Version != Version)
            {
                cached.Pickable = true;
                for (var type = componentType; type != null && type != typeof(EntityComponent); type = type.BaseType)
                    if (excludedTypes.Contains(Key(type))) { cached.Pickable = false; break; }
                cached.Version = Version;
            }
            if (!cached.Pickable) return false;
        }
        return true;
    }
}
