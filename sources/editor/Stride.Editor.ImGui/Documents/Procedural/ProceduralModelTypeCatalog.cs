using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Reflection;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

/// <summary>Picker membership follows actual native plugin registration without rooting retired type lists.</summary>
internal static class ProceduralModelTypeCatalog
{
    private static readonly object Gate = new();
    private static Type[]? types;
    static ProceduralModelTypeCatalog()
    {
        AssemblyRegistry.AssemblyRegistered += Invalidate;
        AssemblyRegistry.AssemblyUnregistered += Invalidate;
    }
    private static void Invalidate(object? sender, AssemblyRegisteredEventArgs args)
    {
        if (!args.Categories.Contains(AssemblyCommonCategories.Assets)) return;
        lock (Gate) types = null;
    }
    internal static IReadOnlyList<Type> Current
    {
        get
        {
            lock (Gate) return types ??= AssemblyRegistry.Find(AssemblyCommonCategories.Assets).SelectMany(assembly => assembly.GetTypes())
                .Where(type => typeof(IProceduralModel).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface && !type.ContainsGenericParameters
                    && !type.IsDefined(typeof(NonInstantiableAttribute), true) && ObjectFactoryRegistry.CanCreateInstance(type))
                .OrderBy(ProceduralAssetDocument.ModelDisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
