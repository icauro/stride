using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed class FontSettingField(IMemberNode node, string key, string label, string category)
{
    public IMemberNode Node { get; } = node;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Category { get; } = category;
    public Type Type => Node.Type;
    public bool AllowsNull => !Type.IsValueType
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null;
    public IReadOnlyList<FontSettingField> Children { get; internal set; } = [];
    public IReadOnlyList<FontSettingItem> Items { get; internal set; } = [];
    public IAssetObjectNode? Collection => Node.Target is IAssetObjectNode { Descriptor: CollectionDescriptor } list ? list : null;
    public IReadOnlyList<Type> Types { get; } = node.Type.IsInterface || node.Type.IsAbstract
        ? node.Type.GetInheritedInstantiableTypes().Where(type => !type.IsDefined(typeof(NonInstantiableAttribute), true) && ObjectFactoryRegistry.CanCreateInstance(type)).OrderBy(Name).ToArray() : [];
    public string[] EnumNames { get; } = node.Type.IsEnum ? Enum.GetNames(node.Type) : [];
    public static string Name(Type type) => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(type)?.Name ?? type.Name;
}
internal sealed record FontSettingItem(ItemId Id, NodeIndex Index, object? Value, IReadOnlyList<FontSettingField> Fields);
