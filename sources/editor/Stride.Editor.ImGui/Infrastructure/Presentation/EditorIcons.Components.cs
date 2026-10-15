using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorIcons
{
    private readonly List<(Type Type, int Order)> componentOrders = [];
    private sealed record ComponentImageName(string Name);
    private readonly ConditionalWeakTable<Type, ComponentImageName> componentImages = new();
    private int componentIconsChanged = 1;

    // Original type keys from Stride.Assets.Presentation.Wpf/View/ImageDictionary.xaml.
    private static readonly Dictionary<string, (string Assembly, string Image)> stockComponentImages = new(StringComparer.Ordinal)
    {
        ["Stride.Engine.EntityComponent"] = ("Stride.Engine", "component-entitycomponent"),
        ["Stride.Engine.AnimationComponent"] = ("Stride.Engine", "component-animationcomponent"),
        ["Stride.Engine.AudioEmitterComponent"] = ("Stride.Engine", "component-audioemittercomponent"),
        ["Stride.Engine.AudioListenerComponent"] = ("Stride.Engine", "component-audiolistenercomponent"),
        ["Stride.Engine.BackgroundComponent"] = ("Stride.Engine", "component-backgroundcomponent"),
        ["Stride.Engine.CameraComponent"] = ("Stride.Engine", "component-cameracomponent"),
        ["Stride.Engine.LightComponent"] = ("Stride.Engine", "component-lightcomponent"),
        ["Stride.Particles.Components.ParticleSystemComponent"] = ("Stride.Particles", "component-particlesystemcomponent"),
        ["Stride.Engine.ModelComponent"] = ("Stride.Engine", "component-modelcomponent"),
        ["Stride.Engine.ModelNodeLinkComponent"] = ("Stride.Engine", "component-modelnodelinkcomponent"),
        ["Stride.Engine.PhysicsComponent"] = ("Stride.Physics", "component-physicscomponent"),
        ["Stride.Physics.PhysicsConstraintComponent"] = ("Stride.Physics", "component-physicsconstraintcomponent"),
        ["Stride.Engine.ScriptComponent"] = ("Stride.Engine", "component-scriptcomponent"),
        ["Stride.Engine.SpriteComponent"] = ("Stride.Engine", "component-spritecomponent"),
        ["Stride.Engine.SpriteStudioComponent"] = ("Stride.SpriteStudio.Runtime", "component-spritestudiocomponent"),
        ["Stride.SpriteStudio.Runtime.SpriteStudioNodeLinkComponent"] = ("Stride.SpriteStudio.Runtime", "component-spritestudionodelinkcomponent"),
        ["Stride.Engine.TransformComponent"] = ("Stride.Engine", "component-transformcomponent"),
        ["Stride.Engine.UIComponent"] = ("Stride.UI", "component-uicomponent"),
        ["Stride.Engine.UIElementLinkComponent"] = ("Stride.UI", "component-uielementlinkcomponent"),
        ["Stride.Video.VideoComponent"] = ("Stride.Video", "component-videocomponent"),
    };

    private void InitializeComponentIcons()
    {
        AssemblyRegistry.AssemblyRegistered += ComponentIconAssemblyChanged;
        AssemblyRegistry.AssemblyUnregistered += ComponentIconAssemblyChanged;
    }

    private void ComponentIconAssemblyChanged(object? sender, AssemblyRegisteredEventArgs args)
        => Interlocked.Exchange(ref componentIconsChanged, 1);

    private void RefreshComponentIcons()
    {
        if (Interlocked.Exchange(ref componentIconsChanged, 0) == 0) return;
        componentOrders.Clear(); componentImages.Clear();
        foreach (var assembly in AssetRegistry.AssetAssemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); }
            foreach (var type in types)
                if (typeof(EntityComponent).IsAssignableFrom(type)
                    && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<ComponentOrderAttribute>(type, false) is { } order)
                    componentOrders.Add((type, order.Order));
        }
        // Native registration reverses first, then performs a stable descending sort.
        componentOrders.Reverse();
        var ordered = componentOrders.OrderByDescending(entry => entry.Order).ToArray();
        componentOrders.Clear(); componentOrders.AddRange(ordered);
    }

    internal string EntityIcon(Entity entity)
    {
        RefreshComponentIcons();
        foreach (var entry in componentOrders)
            foreach (var component in entity.Components)
            {
                var actualType = component.GetType();
                // Exact native ComponentTypeComparer direction, including its subclass matching.
                if (ReferenceEquals(entry.Type, actualType) || entry.Type.IsSubclassOf(actualType))
                    return ComponentIcon(entry.Type);
            }
        return "component-entitycomponent";
    }

    internal string ComponentIcon(Type componentType)
    {
        RefreshComponentIcons();
        if (componentImageProviders != null)
        {
            for (var current = componentType; current != null; current = current.BaseType)
            {
                if (ProviderComponentIcon(current) is { } custom) return custom;
                if (current.FullName is { } name && stockComponentImages.TryGetValue(name, out var stock)
                    && current.Assembly.GetName().Name == stock.Assembly) return stock.Image;
            }
            return "component-entitycomponent";
        }
        if (componentImages.TryGetValue(componentType, out var image)) return image.Name;
        for (var current = componentType; current != null; current = current.BaseType)
            if (current.FullName is { } name && stockComponentImages.TryGetValue(name, out var stock)
                && current.Assembly.GetName().Name == stock.Assembly)
            {
                componentImages.Add(componentType, new(stock.Image));
                return stock.Image;
            }
        componentImages.Add(componentType, new("component-entitycomponent"));
        return "component-entitycomponent";
    }

    private void DisposeComponentIcons()
    {
        AssemblyRegistry.AssemblyRegistered -= ComponentIconAssemblyChanged;
        AssemblyRegistry.AssemblyUnregistered -= ComponentIconAssemblyChanged;
        componentOrders.Clear(); componentImages.Clear();
    }
}
