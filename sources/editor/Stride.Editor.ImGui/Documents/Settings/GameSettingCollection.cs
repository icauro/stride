using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record GameSettingCollection(GameSettingField Origin, IAssetObjectNode Node, GameSettingItem? Parent)
{
    public string Key => Parent?.Key ?? Origin.Key;
    public string Label => Parent?.DisplayName ?? Origin.Label;
    public ITypeDescriptor CollectionDescriptor => Node.Descriptor;
    public MemberCollectionAttribute? CollectionRules => Parent == null ? Origin.CollectionRules
        : TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Node.Type);
    public Type? ElementType => CollectionDescriptor switch
    {
        ArrayDescriptor array => array.ElementType,
        CollectionDescriptor list when list.Category is DescriptorCategory.List or DescriptorCategory.Set => list.ElementType,
        DictionaryDescriptor dictionary => dictionary.ValueType,
        _ => null,
    };
    public bool IsCollection => ElementType != null;
}
