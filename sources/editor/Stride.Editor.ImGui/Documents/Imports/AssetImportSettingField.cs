using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record AssetImportSettingField(IMemberNode Node, string Key, string Label, string Category, int Order)
{
    public Type Type => Nullable.GetUnderlyingType(Node.Type) ?? Node.Type;
    public IReadOnlyList<AssetImportSettingField> Children { get; init; } = [];
    public IReadOnlyList<AssetImportSettingItem> Items { get; internal set; } = [];
    public IAssetObjectNode? IndexedCollection { get; init; }
    public IAssetObjectNode? Collection => IndexedCollection ?? (Node.Target is IAssetObjectNode { IsEnumerable: true } collection ? collection : null);
    public MemberCollectionAttribute? CollectionRules => IndexedCollection != null
        ? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(IndexedCollection.Type)
        : TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Node.MemberDescriptor.MemberInfo)
            ?? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Node.Type);
    public Type? ElementType => Collection?.Descriptor switch
    {
        ArrayDescriptor array => array.ElementType,
        CollectionDescriptor list when list.Category is DescriptorCategory.List or DescriptorCategory.Set => list.ElementType,
        DictionaryDescriptor dictionary => dictionary.ValueType,
        _ => null
    };
    public IReadOnlyList<Type> Types { get; } = (Node.Type.IsInterface || Node.Type.IsAbstract) && !ContentAssetReference.Supports(Node.Type)
        ? Node.Type.GetInheritedInstantiableTypes().Where(type => !type.IsDefined(typeof(NonInstantiableAttribute), true)
            && ObjectFactoryRegistry.CanCreateInstance(type)).OrderBy(type => DisplayAttribute.GetDisplayName(type)).ToArray() : [];
    public bool AllowsNull => Nullable.GetUnderlyingType(Node.Type) != null || !Node.Type.IsValueType
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null;
    public PropertyNumericRange? Range { get; init; }
    public CategoryOrderAttribute? CategoryOrder => TypeDescriptorFactory.Default.AttributeRegistry.GetAttributes<CategoryOrderAttribute>(Node.MemberDescriptor.DeclaringType)
        .FirstOrDefault(category => category.Name == Category);
    public IReadOnlyList<ComponentEnumOption> EnumOptions { get; } = (Nullable.GetUnderlyingType(Node.Type) ?? Node.Type).IsEnum
        ? Enum.GetNames(Nullable.GetUnderlyingType(Node.Type) ?? Node.Type).Select(name =>
        {
            var type = Nullable.GetUnderlyingType(Node.Type) ?? Node.Type;
            var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(type.GetField(name)!);
            return new ComponentEnumOption(Enum.Parse(type, name), name, display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(name));
        }).ToArray() : [];
}

internal sealed record AssetImportSettingItem(AssetImportSettingField Field, IAssetObjectNode Collection, NodeIndex OriginalIndex,
    ItemId? Id, IReadOnlyList<AssetImportSettingField> Children)
{
    public AssetImportSettingField? NestedCollection { get; init; }
    public NodeIndex Index => Id is { } id ? Collection.IdToIndex(id) : OriginalIndex;
    public object? Value => Collection.Retrieve(Index);
    public Type Type => Nullable.GetUnderlyingType(Field.ElementType!) ?? Field.ElementType!;
    public bool AllowsNull => Field.CollectionRules?.NotNullItems != true
        && (!Field.ElementType!.IsValueType || Nullable.GetUnderlyingType(Field.ElementType!) != null);
    public string Key => Field.Key + "/" + (Id?.ToString() ?? OriginalIndex.ToString());
}
