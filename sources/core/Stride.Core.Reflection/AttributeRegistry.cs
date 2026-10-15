// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;

namespace Stride.Core.Reflection;

/// <summary>
/// A default implementation for <see cref="IAttributeRegistry"/>.
/// This implementation allows to retrieve default attributes for a member or
/// to attach an attribute to a specific type/member.
/// </summary>
public partial class AttributeRegistry : IAttributeRegistry
{
    private readonly object lockObject = new();
    private readonly Dictionary<MemberInfoKey, List<Attribute>> cachedAttributes = [];
    private readonly Dictionary<MemberInfo, List<Attribute>> registeredAttributes = [];
    private sealed class CollectibleMemberAttributes
    {
        internal readonly Dictionary<MemberInfoKey, List<Attribute>> Cached = [];
        internal readonly Dictionary<MemberInfo, List<Attribute>> Registered = [];
    }
    private readonly ConditionalWeakTable<Type, CollectibleMemberAttributes> collectibleAttributes = new();
    private CollectibleMemberAttributes? GetCollectibleAttributes(MemberInfo member)
    {
        var type = member as Type ?? (member.ReflectedType?.IsCollectible == true ? member.ReflectedType : member.DeclaringType);
        return type?.IsCollectible == true ? collectibleAttributes.GetValue(type, _ => new()) : null;
    }


    // TODO: move this in a different location
    public Action<ObjectDescriptor, List<IMemberDescriptor>>? PrepareMembersCallback { get; set; }

    /// <summary>
    /// Gets the attributes associated with the specified member.
    /// </summary>
    /// <param name="memberInfo">The reflection member.</param>
    /// <param name="inherit">if set to <c>true</c> includes inherited attributes.</param>
    /// <returns>An enumeration of <see cref="Attribute"/>.</returns>
    public virtual List<Attribute> GetAttributes(MemberInfo memberInfo, bool inherit = true)
    {
        var key = new MemberInfoKey(memberInfo, inherit);

        // Use a cache of attributes
        List<Attribute> attributes;
        lock (lockObject)
        {
            var generation = GetCollectibleAttributes(memberInfo);
            var cache = generation?.Cached ?? cachedAttributes;
            var registrations = generation?.Registered ?? registeredAttributes;
            if (cache.TryGetValue(key, out var cacheAttributes))
            {
                return cacheAttributes;
            }

            // Else retrieve all default attributes
            var defaultAttributes = Attribute.GetCustomAttributes(memberInfo, inherit);
            IEnumerable<Attribute> attributesToCache = defaultAttributes;

            // And add registered attributes
            if (registrations.TryGetValue(memberInfo, out var registered))
            {
                // Remove "real" attributes overridden by manually registered attributes
                attributesToCache = registered.Concat(defaultAttributes.Where(x => GetUsage(x)!.AllowMultiple || registered.All(y => y.GetType() != x.GetType())));
            }

            attributes = attributesToCache.ToList();

            // Add to the cache
            cache.Add(key, attributes);
        }

        return attributes;
    }

    /// <summary>
    /// Registers an attribute for the specified member. Restriction: Attributes registered this way cannot be listed in inherited attributes.
    /// </summary>
    /// <param name="memberInfo">The member information.</param>
    /// <param name="attribute">The attribute.</param>
    public void Register(MemberInfo memberInfo, Attribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        // Baked attributes have no external registration owner. Manually attached
        // custom attributes do; preserve them until that actual generation unloads.
        var owner = attribute is Stride.Core.Annotations.ObjectFactoryAttribute factory
            ? factory.FactoryType.Assembly : attribute.GetType().Assembly;
        Register(memberInfo, attribute, owner);
    }

    private static AttributeUsageAttribute? GetUsage(Attribute attribute)
    {
        return Attribute.GetCustomAttribute(attribute.GetType(), typeof(AttributeUsageAttribute)) as AttributeUsageAttribute;
    }

    private readonly struct MemberInfoKey : IEquatable<MemberInfoKey>
    {
        private readonly MemberInfo memberInfo;

        private readonly bool inherit;

        public MemberInfoKey(MemberInfo memberInfo, bool inherit)
        {
            ArgumentNullException.ThrowIfNull(memberInfo);
            this.memberInfo = memberInfo;
            this.inherit = inherit;
        }

        public bool Equals(MemberInfoKey other)
        {
            return memberInfo.Equals(other.memberInfo) && inherit.Equals(other.inherit);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is MemberInfoKey key && Equals(key);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (memberInfo.GetHashCode()*397) ^ inherit.GetHashCode();
            }
        }
    }
}
