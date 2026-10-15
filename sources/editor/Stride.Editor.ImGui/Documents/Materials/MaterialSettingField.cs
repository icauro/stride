using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed class MaterialSettingField(IMemberNode node, string key, string label, string category)
{
    public IMemberNode Node { get; } = node;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Category { get; } = category;
    public Type Type => Nullable.GetUnderlyingType(Node.Type) ?? Node.Type;
    internal static bool SupportsNullableSpecial(Type type) => type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(DateTime)
        || type == typeof(Stride.Core.Mathematics.AngleSingle) || type == typeof(Stride.Core.Mathematics.Quaternion)
        || type == typeof(Stride.Core.Mathematics.Int2) || type == typeof(Stride.Core.Mathematics.Int3) || type == typeof(Stride.Core.Mathematics.Int4)
        || type == typeof(Stride.Core.Mathematics.Matrix) || type == typeof(Stride.Core.Mathematics.Rectangle) || type == typeof(Stride.Core.Mathematics.RectangleF);
    public IReadOnlyList<MaterialSettingField> Children { get; internal set; } = [];
    public AssetImportSettingField? CollectionField { get; internal set; }
    public bool AllowsNull => (Nullable.GetUnderlyingType(Node.Type) != null || !Node.Type.IsValueType)
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null;
    public IReadOnlyList<Type> Types { get; } = (node.Type.IsInterface || node.Type.IsAbstract) && !ContentAssetReference.Supports(node.Type)
        ? node.Type.GetInheritedInstantiableTypes()
            .Where(type => !type.IsDefined(typeof(NonInstantiableAttribute), true) && ObjectFactoryRegistry.CanCreateInstance(type))
            .OrderBy(Name, StringComparer.OrdinalIgnoreCase).ToArray() : [];
    public bool CanAssign => AssetMetadataDocument.CanAssignAuthoringNode(Node);
    public PropertyNumericRange? NumericRange { get; } = AssetSettingNumericMetadata.Read(node);
    public IReadOnlyList<ComponentEnumOption> EnumOptions { get; } = (Nullable.GetUnderlyingType(node.Type) ?? node.Type).IsEnum ? Enum.GetNames(Nullable.GetUnderlyingType(node.Type) ?? node.Type).Select(name =>
        new ComponentEnumOption(Enum.Parse(Nullable.GetUnderlyingType(node.Type) ?? node.Type, name), name,
            TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>((Nullable.GetUnderlyingType(node.Type) ?? node.Type).GetField(name)!)?.Name
                ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(name))).ToArray() : [];
    public static string Name(Type type) => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(type)?.Name ?? type.Name;
}
