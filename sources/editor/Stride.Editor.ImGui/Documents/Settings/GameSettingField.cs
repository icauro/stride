using Stride.Core;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Rules;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record GameSettingField(IMemberNode Node, string Key, string Label, string Category, int Order)
{
    public ExpandRule Expand => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(Node.MemberDescriptor.MemberInfo)?.Expand is { } member && member != ExpandRule.Auto
        ? member : TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(Node.Retrieve()?.GetType() ?? Node.Type)?.Expand ?? ExpandRule.Auto;
    public CategoryOrderAttribute? CategoryMetadata => TypeDescriptorFactory.Default.AttributeRegistry.GetAttributes<CategoryOrderAttribute>(Node.MemberDescriptor.DeclaringType)
        .FirstOrDefault(category => category.Name == Category);
    private (string? Name, ExpandRule Expand) InlineDefinition { get; } = InlineMemberRules.FindMainProperty(Node.Retrieve()?.GetType() ?? Node.Type);
    public GameSettingField? InlineMember => Children.FirstOrDefault(member => member.Node.Name == InlineDefinition.Name);
    public GameSettingField? EnabledMember => Children.FirstOrDefault(member => member.Node.Name == "Enabled" && member.Type == typeof(bool));
    public ExpandRule InlineExpansion => InlineDefinition.Expand;
    public Type Type => Nullable.GetUnderlyingType(Node.Type) ?? Node.Type;
    public ITypeDescriptor CollectionDescriptor { get; } = TypeDescriptorFactory.Default.Find(Node.Type);
    public MemberCollectionAttribute? CollectionRules { get; } = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Node.MemberDescriptor.MemberInfo)
        ?? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Node.Type);
    public Type? ElementType => CollectionDescriptor switch
    {
        ArrayDescriptor array => array.ElementType, CollectionDescriptor list when list.Category is DescriptorCategory.List or DescriptorCategory.Set => list.ElementType,
        DictionaryDescriptor dictionary => dictionary.ValueType, _ => null,
    };
    public bool IsCollection => ElementType != null;
    public bool CanAssign => AssetMetadataDocument.CanAssignAuthoringNode(Node);
    public IReadOnlyList<ComponentEnumOption> EnumOptions { get; } = (Nullable.GetUnderlyingType(Node.Type) ?? Node.Type).IsEnum ? Enum.GetNames(Nullable.GetUnderlyingType(Node.Type) ?? Node.Type).Select(name =>
    {
        var metadata = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>((Nullable.GetUnderlyingType(Node.Type) ?? Node.Type).GetField(name)!);
        return new ComponentEnumOption(Enum.Parse(Nullable.GetUnderlyingType(Node.Type) ?? Node.Type, name), name, metadata?.Name ?? name);
    }).ToArray() : [];
    public bool AllowsNull { get; } = !HasNotNullMetadata(Node);
    public IReadOnlyList<GameSettingField> Children { get; init; } = [];
    public IReadOnlyList<PropertyNumericRange?> AxisRanges { get; init; } = VectorPropertyNumericMetadata.Create(Node);
    public PropertyNumericRange? Range { get; init; } = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DataMemberRangeAttribute>(Node.MemberDescriptor.MemberInfo) is { } range
        && ((Nullable.GetUnderlyingType(Node.Type) ?? Node.Type).IsPrimitive || (Nullable.GetUnderlyingType(Node.Type) ?? Node.Type) == typeof(decimal))
        ? new(range, Nullable.GetUnderlyingType(Node.Type) ?? Node.Type) : null;
    public IReadOnlyList<Type> Types { get; } = Node.Type.IsInterface || Node.Type.IsAbstract
        ? Node.Type.GetInheritedInstantiableTypes().Where(type => !type.IsDefined(typeof(NonInstantiableAttribute), true)
            && !ContentAssetReference.Supports(type) && !SceneObjectReference.Supports(type) && ObjectFactoryRegistry.CanCreateInstance(type)).ToArray() : [];
    private static bool HasNotNullMetadata(IMemberNode node) => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(node.MemberDescriptor.MemberInfo) != null
        || TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(node.MemberDescriptor.MemberInfo) != null;
}
