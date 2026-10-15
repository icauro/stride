// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Stride.Core;

public partial struct PropertyContainer
{
    private sealed class AccessorRegistration(Type type, PropertyKey key)
    {
        internal readonly WeakReference<Type> Target = new(type);
        internal readonly PropertyKey Key = key;
    }

    private sealed class AccessorSet
    {
        internal readonly List<AccessorRegistration> Registrations = [];
        internal PropertyKey[] Keys = [];
        internal void Refresh() => Volatile.Write(ref Keys, Registrations.Select(x => x.Key).ToArray());
    }

    private sealed class AccessorGeneration
    {
        internal int Unloading;
        internal readonly List<WeakReference<AccessorRegistration>> Registrations = [];
    }

    private static readonly object AccessorGate = new();
    private static readonly ConditionalWeakTable<Type, AccessorSet> AccessorProperties = new();
    private static readonly ConditionalWeakTable<AssemblyLoadContext, AccessorGeneration> AccessorGenerations = new();

    private static PropertyKey[] GetAccessorProperties(Type type) =>
        AccessorProperties.TryGetValue(type, out var properties) ? Volatile.Read(ref properties.Keys) : [];

    public static void AddAccessorProperty(Type type, PropertyKey propertyKey) => AddAccessorProperty(type, propertyKey, null);

    /// <summary>
    /// Registers an accessor while its actual provider generation is available.
    /// Specify the owner for builtin keys with provider-owned metadata values.
    /// </summary>
    public static void AddAccessorProperty(Type type, PropertyKey propertyKey, Assembly? ownerAssembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(propertyKey);
        if (!type.GetTypeInfo().IsClass)
            throw new ArgumentException("Class type is expected.", nameof(type));
        if (propertyKey.AccessorMetadata == null)
            throw new ArgumentException("Given PropertyKey doesn't have accessor metadata.", nameof(propertyKey));

        var assemblies = new HashSet<Assembly>();
        var visitedTypes = new HashSet<Type>();
        void IncludeType(Type value)
        {
            if (!visitedTypes.Add(value)) return;
            assemblies.Add(value.Assembly);
            if (value.HasElementType && value.GetElementType() is { } element) IncludeType(element);
            foreach (var argument in value.GetGenericArguments()) IncludeType(argument);
        }
        IncludeType(type);
        IncludeType(propertyKey.OwnerType);
        IncludeType(propertyKey.PropertyType);
        IncludeType(propertyKey.GetType());
        foreach (var value in propertyKey.AccessorMetadata.RegistrationTypes()) IncludeType(value);
        assemblies.UnionWith(propertyKey.AccessorMetadata.RegistrationAssemblies());
        foreach (var metadata in propertyKey.Metadatas) IncludeType(metadata.GetType());
        if (ownerAssembly != null) assemblies.Add(ownerAssembly);
        var contexts = assemblies.Select(AssemblyLoadContext.GetLoadContext)
            .Where(x => x?.IsCollectible == true).Cast<AssemblyLoadContext>().Distinct().ToArray();

        lock (AccessorGate)
        {
            var generations = new List<AccessorGeneration>(contexts.Length);
            foreach (var context in contexts)
            {
                if (!AccessorGenerations.TryGetValue(context, out var generation))
                {
                    generation = new AccessorGeneration();
                    AccessorGenerations.Add(context, generation);
                    var observed = generation;
                    context.Unloading += _ =>
                    {
                        Interlocked.Exchange(ref observed.Unloading, 1);
                        RetireAccessorProperties(observed);
                    };
                }
                if (Volatile.Read(ref generation.Unloading) != 0)
                    throw new InvalidOperationException("The accessor provider generation is unloading.");
                generations.Add(generation);
            }

            var registration = new AccessorRegistration(type, propertyKey);
            var properties = AccessorProperties.GetValue(type, _ => new());
            properties.Registrations.Add(registration);
            properties.Refresh();
            foreach (var generation in generations)
                generation.Registrations.Add(new WeakReference<AccessorRegistration>(registration));
        }
    }

    private static void RetireAccessorProperties(AccessorGeneration generation)
    {
        lock (AccessorGate)
        {
            foreach (var weak in generation.Registrations)
            {
                if (!weak.TryGetTarget(out var registration) || !registration.Target.TryGetTarget(out var type)
                    || !AccessorProperties.TryGetValue(type, out var properties)) continue;
                if (!properties.Registrations.Remove(registration)) continue;
                properties.Refresh();
                if (properties.Registrations.Count == 0) AccessorProperties.Remove(type);
            }
            generation.Registrations.Clear();
        }
    }
}
