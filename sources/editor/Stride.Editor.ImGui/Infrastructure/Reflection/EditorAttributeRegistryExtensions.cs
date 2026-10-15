using System.Reflection;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal static class EditorAttributeRegistryExtensions
{
    internal static void Register(this IAttributeRegistry registry, MemberInfo member, Attribute attribute, Assembly ownerAssembly)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry is not AttributeRegistry nativeRegistry)
            throw new NotSupportedException("This attribute registry does not support assembly-owned registrations.");
        nativeRegistry.Register(member, attribute, ownerAssembly);
    }
}
