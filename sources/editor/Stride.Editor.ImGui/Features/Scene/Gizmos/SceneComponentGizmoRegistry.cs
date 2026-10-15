using System.Reflection;
using Stride.Core;
using Stride.Core.Reflection;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Assets.Presentation.AssetEditors.Gizmos;

namespace Stride.Editor.ImGui;

internal sealed class SceneComponentGizmoRegistry : IDisposable
{
    internal sealed record Family(Type ComponentType, Type GeometryType, bool Main)
    {
        internal string Name => DisplayAttribute.GetDisplayName(ComponentType) ?? ComponentType.Name;
        internal string Id => ComponentType.FullName!;
        internal bool IsFallback => ComponentType == typeof(TransformComponent);
    }
    private Family[] families = [];
    private int changed = 1;
    private long providerVersion = -1;
    private bool pruneRetired;
    private readonly Dictionary<Type, Family?> lookup = [];
    private readonly Dictionary<Type, int> orders = [];
    internal IReadOnlyList<Family> Families => families;
    internal SceneComponentGizmoRegistry()
    {
        AssemblyRegistry.AssemblyRegistered += AssemblyChanged;
        AssemblyRegistry.AssemblyUnregistered += AssemblyChanged;
        Refresh();
    }
    private void AssemblyChanged(object? sender, AssemblyRegisteredEventArgs args) => Interlocked.Exchange(ref changed, 1);
    internal void Refresh(long? requestedProviderVersion = null)
    {
        var currentProviderVersion = requestedProviderVersion ?? providerVersion;
        if (Interlocked.Exchange(ref changed, 0) == 0 && providerVersion == currentProviderVersion) return;
        var found = new Dictionary<Type, Family>();
        var assemblies = AssemblyRegistry.FindAll();
        assemblies.Add(typeof(SceneComponentGizmoRegistry).Assembly);
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); }
            foreach (var type in types)
                if (!type.IsAbstract && (type.IsPublic || type.IsNestedPublic) && typeof(EntityGizmo).IsAssignableFrom(type)
                    && type.GetCustomAttribute<GizmoComponentAttribute>(false) is { } attribute)
                    found[attribute.ComponentType] = new(attribute.ComponentType, type, attribute.IsMainGizmo);
        }
        // The native light dispatcher selects the actual geometry for the live light type.
        found[typeof(LightComponent)] = new(typeof(LightComponent), typeof(LightGizmo), true);
        found[typeof(TransformComponent)] = new(typeof(TransformComponent), typeof(FallbackGizmo), true);
        families = found.Values.OrderBy(family => family.Name, StringComparer.CurrentCulture).ToArray();
        // Previously resolved component generations remain valid while their runtime owners are retained.
        foreach (var key in lookup.Keys.ToArray())
            if (lookup[key] == null || assemblies.Contains(key.Assembly)) lookup.Remove(key);
        orders.Clear(); providerVersion = currentProviderVersion;
        pruneRetired = true;
    }
    internal Family? Resolve(Type component)
    {
        if (lookup.TryGetValue(component, out var cached)) return cached;
        for (var current = component; current != null; current = current.BaseType)
            if (families.FirstOrDefault(family => family.ComponentType == current) is { } family)
                return lookup[component] = family;
        return lookup[component] = null;
    }
    internal int Order(Type component)
    {
        if (!orders.TryGetValue(component, out var order)) orders.Add(component, order = TypeDescriptorFactory.Default.AttributeRegistry
            .GetAttribute<ComponentOrderAttribute>(component, false)?.Order ?? 0);
        return order;
    }
    internal void ReleaseUnusedGenerations(IEnumerable<EntityComponent> activeComponents)
    {
        if (!pruneRetired) return;
        var active = activeComponents.Select(component => component.GetType()).ToHashSet();
        var assemblies = AssemblyRegistry.FindAll(); assemblies.Add(typeof(SceneComponentGizmoRegistry).Assembly);
        foreach (var type in lookup.Keys.ToArray())
            if (!active.Contains(type) && !assemblies.Contains(type.Assembly)) { lookup.Remove(type); orders.Remove(type); }
        pruneRetired = false;
    }
    internal void RequestPrune() => pruneRetired = true;
    public void Dispose()
    {
        AssemblyRegistry.AssemblyRegistered -= AssemblyChanged;
        AssemblyRegistry.AssemblyUnregistered -= AssemblyChanged;
        lookup.Clear(); orders.Clear(); families = [];
    }
}
