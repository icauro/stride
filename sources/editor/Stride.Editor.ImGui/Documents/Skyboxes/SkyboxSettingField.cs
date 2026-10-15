using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record SkyboxSettingField(IMemberNode Node, string Label)
{
    public Type Type => Node.Type;
    public bool CanAssign => AssetMetadataDocument.CanAssignAuthoringNode(Node);
    public bool AllowsNull { get; } = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null;
    public PropertyNumericRange? Range { get; } = AssetSettingNumericMetadata.Read(Node);
    public IReadOnlyList<ComponentEnumOption> EnumOptions { get; } = Node.Type.IsEnum ? Enum.GetNames(Node.Type).Select(name =>
        new ComponentEnumOption(Enum.Parse(Node.Type, name), name,
            TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(Node.Type.GetField(name)!)?.Name ?? name)).ToArray() : [];
}
