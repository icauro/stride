using Stride.Assets.Scripts;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record VisualScriptBlockTemplate(Type Type, string Name, string Category)
{
    internal Block Create() => (Block)(Activator.CreateInstance(Type) ?? throw new InvalidOperationException("The registered block factory returned no instance."));
    internal static VisualScriptBlockTemplate[] Discover() => AssemblyRegistry.FindAll().Append(typeof(Block).Assembly).Distinct()
        .SelectMany(assembly => assembly.GetTypes()).Where(type => !type.IsAbstract && typeof(Block).IsAssignableFrom(type)
            && type != typeof(MethodCallBlock) && type.GetConstructor(Type.EmptyTypes) != null
            && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<ObsoleteAttribute>(type) == null
            && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NonInstantiableAttribute>(type) == null)
        .Select(type => new VisualScriptBlockTemplate(type, type.Name,
            TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(type)?.Category ?? "Misc"))
        .OrderBy(template => template.Category, StringComparer.Ordinal).ThenBy(template => template.Name, StringComparer.Ordinal).ToArray();
}
