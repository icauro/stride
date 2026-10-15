// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Stride.Core.Reflection;

public partial class AttributeRegistry
{
    private sealed class GenerationAttributes
    {
        internal int Unloading;
        internal readonly List<(MemberInfo Member, Attribute Attribute)> Registrations = [];
    }

    private readonly ConditionalWeakTable<AssemblyLoadContext, GenerationAttributes> attributeGenerations = new();
    private readonly ConditionalWeakTable<TypeDescriptorFactory, object> descriptorFactories = new();

    internal void TrackDescriptors(TypeDescriptorFactory factory) => descriptorFactories.GetValue(factory, _ => new object());

    /// <summary>
    /// Registers metadata owned by an actual assembly generation. Use this overload
    /// when a builtin attribute carries values supplied by a collectible provider.
    /// The registration remains strong until that owner's load context unloads.
    /// </summary>
    public void Register(MemberInfo memberInfo, Attribute attribute, Assembly ownerAssembly)
    {
        ArgumentNullException.ThrowIfNull(memberInfo);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        var context = AssemblyLoadContext.GetLoadContext(ownerAssembly);
        var attributeContext = AssemblyLoadContext.GetLoadContext(attribute.GetType().Assembly);
        if (attributeContext?.IsCollectible == true && !ReferenceEquals(attributeContext, context))
            throw new ArgumentException("The registration owner must own the collectible attribute generation.", nameof(ownerAssembly));

        lock (lockObject)
        {
            GenerationAttributes? ownership = null;
            if (context?.IsCollectible == true)
            {
                if (!attributeGenerations.TryGetValue(context, out ownership))
                {
                    ownership = new GenerationAttributes();
                    attributeGenerations.Add(context, ownership);
                    var weakRegistry = new WeakReference<AttributeRegistry>(this);
                    var observed = ownership;
                    context.Unloading += _ =>
                    {
                        Interlocked.Exchange(ref observed.Unloading, 1);
                        if (weakRegistry.TryGetTarget(out var registry)) registry.RetireAttributes(observed);
                        else observed.Registrations.Clear();
                    };
                }
                if (Volatile.Read(ref ownership.Unloading) != 0)
                    throw new InvalidOperationException("The metadata owner generation is unloading.");
            }

            var generation = GetCollectibleAttributes(memberInfo);
            var registrations = generation?.Registered ?? registeredAttributes;
            if (!registrations.TryGetValue(memberInfo, out var attributes))
                registrations.Add(memberInfo, attributes = []);
            // Preserve the original override ordering and exact instance identity.
            attributes.Insert(0, attribute);
            ownership?.Registrations.Add((memberInfo, attribute));
            InvalidateAttributes(memberInfo, generation);
        }
    }

    private void InvalidateAttributes(MemberInfo member, CollectibleMemberAttributes? generation)
    {
        var cache = generation?.Cached ?? cachedAttributes;
        cache.Remove(new MemberInfoKey(member, true));
        cache.Remove(new MemberInfoKey(member, false));
    }

    private void RetireAttributes(GenerationAttributes ownership)
    {
        MemberInfo[] affected;
        lock (lockObject)
        {
            affected = ownership.Registrations.Select(x => x.Member).Distinct().ToArray();
            foreach (var (member, attribute) in ownership.Registrations)
            {
                var generation = GetCollectibleAttributes(member);
                var registrations = generation?.Registered ?? registeredAttributes;
                if (registrations.TryGetValue(member, out var attributes))
                {
                    // Never remove another provider's replacement or same-type value.
                    var index = attributes.FindIndex(x => ReferenceEquals(x, attribute));
                    if (index >= 0) attributes.RemoveAt(index);
                    if (attributes.Count == 0) registrations.Remove(member);
                }
                InvalidateAttributes(member, generation);
            }
            ownership.Registrations.Clear();
        }

        // No reverse factory/registry lock. CWT removal is thread safe; descriptors
        // already held by a document/history retain their original metadata.
        foreach (var factory in descriptorFactories)
            foreach (var member in affected)
                factory.Key.RetireRegisteredAttributes(member);
    }
}
