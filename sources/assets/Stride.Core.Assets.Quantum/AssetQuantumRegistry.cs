// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;

namespace Stride.Core.Assets.Quantum;

public static class AssetQuantumRegistry
{
    private static readonly object Gate = new();
    private static readonly Type[] AssetPropertyNodeGraphConstructorSignature = [typeof(AssetPropertyGraphContainer), typeof(AssetItem), typeof(ILogger)];
    private static readonly ConditionalWeakTable<Type, Type> NodeGraphTypes = new();
    private static readonly ConditionalWeakTable<Type, AssetPropertyGraphDefinition> NodeGraphDefinitions = new();
    private static readonly ConditionalWeakTable<Type, Type> GenericNodeGraphDefinitionTypes = new();
    private static ConditionalWeakTable<Type, AssetPropertyGraphDefinition> resolvedDefinitions = new();
    private static readonly ConditionalWeakTable<Assembly, AssemblyRegistration> RegisteredAssemblies = new();
    private sealed class AssemblyRegistration
    {
        internal readonly List<(Type Asset, Type Graph)> Graphs = [];
        internal readonly List<(Type Asset, AssetPropertyGraphDefinition Definition)> Definitions = [];
        internal readonly List<(Type Asset, Type Definition)> GenericDefinitions = [];
        internal int Unloading;
        internal WeakReference<AssemblyLoadContext>? LoadContext;
        internal Action<AssemblyLoadContext>? UnloadingHandler;
    }
    static AssetQuantumRegistry()
    {
        foreach (var assembly in AssemblyRegistry.Find(AssemblyCommonCategories.Assets)) RegisterAssembly(assembly);
        AssemblyRegistry.AssemblyRegistered += (_, e) =>
        {
            if (e.Categories.Contains(AssemblyCommonCategories.Assets)) RegisterAssembly(e.Assembly);
        };
        AssemblyRegistry.AssemblyUnregistered += (_, e) =>
        {
            if (e.Categories.Contains(AssemblyCommonCategories.Assets)) UnregisterAssembly(e.Assembly);
        };
    }
    /// <summary>Forgets a plugin generation and its cached graph definitions.</summary>
    public static void UnregisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Gate)
        {
            if (RegisteredAssemblies.TryGetValue(assembly, out var registration))
            {
                RetireAssembly(registration);
                if (registration.LoadContext is { } weak && weak.TryGetTarget(out var owner) && registration.UnloadingHandler != null)
                    owner.Unloading -= registration.UnloadingHandler;
                registration.UnloadingHandler = null;
                registration.LoadContext = null;
                RegisteredAssemblies.Remove(assembly);
            }
            // A graph registered by another assembly can still target a type from this plugin.
            foreach (var entry in NodeGraphTypes.ToArray())
                if (IsOf(entry.Key) || IsOf(entry.Value)) NodeGraphTypes.Remove(entry.Key);
            foreach (var entry in NodeGraphDefinitions.ToArray())
                if (IsOf(entry.Key) || IsOf(entry.Value.GetType())) NodeGraphDefinitions.Remove(entry.Key);
            foreach (var entry in GenericNodeGraphDefinitionTypes.ToArray())
                if (IsOf(entry.Key) || IsOf(entry.Value)) GenericNodeGraphDefinitionTypes.Remove(entry.Key);
            resolvedDefinitions = new();
        }
        bool IsOf(Type type) => type.Assembly == assembly || (type.IsGenericType && type.GetGenericArguments().Any(IsOf));
    }

    public static void RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Gate)
        {
            if (RegisteredAssemblies.TryGetValue(assembly, out var registered))
            {
                if (Volatile.Read(ref registered.Unloading) != 0) throw new InvalidOperationException("The asset graph assembly is unloading.");
                return;
            }
            var registration = new AssemblyRegistration();
            RegisteredAssemblies.Add(assembly, registration);
            if (AssemblyLoadContext.GetLoadContext(assembly) is { IsCollectible: true } loadContext)
            {
                registration.LoadContext = new(loadContext);
                registration.UnloadingHandler = _ => RetireAssembly(registration);
                loadContext.Unloading += registration.UnloadingHandler;
            }
            try
            {
                // The real processor index avoids loading unrelated MSBuild/editor types.
                var scanTypes = AssemblyRegistry.GetScanTypes(assembly);
                if (scanTypes == null) return;
                if (scanTypes.Types.TryGetValue(typeof(AssetPropertyGraphAttribute), out var graphs))
                    foreach (var type in graphs)
                    {
                        var attribute = type.GetCustomAttribute<AssetPropertyGraphAttribute>();
                        if (attribute == null) continue;
                        if (type.GetConstructor(AssetPropertyNodeGraphConstructorSignature) == null)
                            throw new InvalidOperationException($"The type {type.Name} does not have a public constructor matching the expected signature: ({string.Join(", ", (IEnumerable<Type>)AssetPropertyNodeGraphConstructorSignature)})");
                        if (NodeGraphTypes.TryGetValue(attribute.AssetType, out _) || registration.Graphs.Any(entry => entry.Asset == attribute.AssetType))
                            throw new ArgumentException($"The type {attribute.AssetType.Name} already has an associated property node graph type.");
                        registration.Graphs.Add((attribute.AssetType, type));
                    }
                if (scanTypes.Types.TryGetValue(typeof(AssetPropertyGraphDefinitionAttribute), out var definitions))
                    foreach (var type in definitions)
                    {
                        var attribute = type.GetCustomAttribute<AssetPropertyGraphDefinitionAttribute>();
                        if (attribute == null) continue;
                        if (type.GetConstructor(Type.EmptyTypes) == null)
                            throw new InvalidOperationException($"The type {type.Name} does not have a public parameterless constructor.");
                        if (NodeGraphDefinitions.TryGetValue(attribute.AssetType, out _) || GenericNodeGraphDefinitionTypes.TryGetValue(attribute.AssetType, out _)
                            || registration.Definitions.Any(entry => entry.Asset == attribute.AssetType) || registration.GenericDefinitions.Any(entry => entry.Asset == attribute.AssetType))
                            throw new ArgumentException($"The type {attribute.AssetType.Name} already has an associated property node graph definition.");
                        if (attribute.AssetType.IsGenericTypeDefinition && type.IsGenericType)
                            registration.GenericDefinitions.Add((attribute.AssetType, type.GetGenericTypeDefinition()));
                        else registration.Definitions.Add((attribute.AssetType, (AssetPropertyGraphDefinition)Activator.CreateInstance(type)!));
                    }
                if (Volatile.Read(ref registration.Unloading) != 0) throw new InvalidOperationException("The asset graph assembly unloaded during registration.");
                foreach (var entry in registration.Graphs) NodeGraphTypes.Add(entry.Asset, entry.Graph);
                foreach (var entry in registration.Definitions) NodeGraphDefinitions.Add(entry.Asset, entry.Definition);
                foreach (var entry in registration.GenericDefinitions) GenericNodeGraphDefinitionTypes.Add(entry.Asset, entry.Definition);
                resolvedDefinitions = new();
            }
            catch
            {
                RemoveEntries(registration);
                if (Volatile.Read(ref registration.Unloading) == 0)
                {
                    if (registration.LoadContext is { } weak && weak.TryGetTarget(out var owner) && registration.UnloadingHandler != null)
                        owner.Unloading -= registration.UnloadingHandler;
                    registration.UnloadingHandler = null; registration.LoadContext = null;
                    RegisteredAssemblies.Remove(assembly);
                }
                throw;
            }
        }
    }
    private static void RetireAssembly(AssemblyRegistration registration)
    {
        Interlocked.Exchange(ref registration.Unloading, 1);
        lock (Gate) RemoveEntries(registration);
    }
    private static void RemoveEntries(AssemblyRegistration registration)
    {
        foreach (var entry in registration.Graphs)
            if (NodeGraphTypes.TryGetValue(entry.Asset, out var graph) && ReferenceEquals(graph, entry.Graph)) NodeGraphTypes.Remove(entry.Asset);
        foreach (var entry in registration.Definitions)
            if (NodeGraphDefinitions.TryGetValue(entry.Asset, out var definition) && ReferenceEquals(definition, entry.Definition)) NodeGraphDefinitions.Remove(entry.Asset);
        foreach (var entry in registration.GenericDefinitions)
            if (GenericNodeGraphDefinitionTypes.TryGetValue(entry.Asset, out var definition) && ReferenceEquals(definition, entry.Definition)) GenericNodeGraphDefinitionTypes.Remove(entry.Asset);
        registration.Graphs.Clear(); registration.Definitions.Clear(); registration.GenericDefinitions.Clear();
        resolvedDefinitions = new();
    }
    private static void EnsureGeneration(Type assetType)
    {
        if (RegisteredAssemblies.TryGetValue(assetType.Assembly, out var registration) && Volatile.Read(ref registration.Unloading) != 0)
            throw new InvalidOperationException("Cannot construct an asset graph for an unloading assembly generation.");
    }
    public static AssetPropertyGraph ConstructPropertyGraph(AssetPropertyGraphContainer container, AssetItem assetItem, ILogger? logger)
    {
        lock (Gate)
        {
            EnsureGeneration(assetItem.Asset.GetType());
            for (var assetType = assetItem.Asset.GetType(); assetType != null; assetType = assetType.BaseType)
            {
                var typeToTest = assetType.IsGenericType ? assetType.GetGenericTypeDefinition() : assetType;
                if (NodeGraphTypes.TryGetValue(typeToTest, out var graphType))
                    return (AssetPropertyGraph)Activator.CreateInstance(graphType, container, assetItem, logger)!;
            }
            throw new InvalidOperationException("No AssetPropertyGraph type matching the given asset type has been found");
        }
    }
    public static AssetPropertyGraphDefinition GetDefinition(Type assetType)
    {
        if (!typeof(Asset).IsAssignableFrom(assetType)) throw new ArgumentException($"The type {assetType.Name} is not an asset type");
        lock (Gate)
        {
            EnsureGeneration(assetType);
            if (resolvedDefinitions.TryGetValue(assetType, out var cached)) return cached;
            for (var current = assetType; current != null && current != typeof(Asset); current = current.BaseType)
            {
                if (NodeGraphDefinitions.TryGetValue(current, out var definition))
                { resolvedDefinitions.Add(assetType, definition); return definition; }
                if (current.IsGenericType && GenericNodeGraphDefinitionTypes.TryGetValue(current.GetGenericTypeDefinition(), out var genericDefinition))
                {
                    try
                    {
                        var constructed = genericDefinition.MakeGenericType(current.GetGenericArguments());
                        definition = (AssetPropertyGraphDefinition)Activator.CreateInstance(constructed)!;
                        resolvedDefinitions.Add(assetType, definition); return definition;
                    }
                    catch (Exception error)
                    { throw new InvalidOperationException($"Unable to create an instance of definition type {genericDefinition.Name} for asset type {current.Name}.", error); }
                }
            }
            if (!NodeGraphDefinitions.TryGetValue(typeof(Asset), out var fallback)) throw new InvalidOperationException("The default asset graph definition is not registered.");
            resolvedDefinitions.Add(assetType, fallback); return fallback;
        }
    }
}
