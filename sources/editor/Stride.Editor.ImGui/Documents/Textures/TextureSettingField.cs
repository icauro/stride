using Stride.Core;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record TextureSettingField(IMemberNode Node, string Key, string Label, string Category)
{
    public Type Type => Node.Type;
    public string[] EnumNames { get; } = Node.Type.IsEnum ? Enum.GetNames(Node.Type) : [];
    public string[] EnumLabels { get; } = Node.Type.IsEnum ? Enum.GetNames(Node.Type).Select(name =>
        TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(Node.Type.GetField(name)!)?.Name ?? name).ToArray() : [];
}
