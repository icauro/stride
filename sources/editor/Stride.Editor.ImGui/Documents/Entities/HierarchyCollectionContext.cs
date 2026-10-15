using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

// Describes an actual nested Quantum collection; it owns no alternate values or nodes.
internal sealed record HierarchyCollectionContext(ComponentField Origin, IAssetObjectNode Node, HierarchyCollectionItem? Parent, long StructureVersion)
{
    public ITypeDescriptor Descriptor => Node.Descriptor;
    public MemberCollectionAttribute? Rules => Parent == null ? Origin.CollectionRules
        : TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Node.Type);
    public Type? ElementType => Descriptor switch
    {
        ArrayDescriptor array => array.ElementType,
        CollectionDescriptor list when list.Category is DescriptorCategory.List or DescriptorCategory.Set => list.ElementType,
        DictionaryDescriptor dictionary => dictionary.ValueType,
        _ => null,
    };
    public string Key => Parent?.IdentityKey ?? Origin.Component.Id + "/" + Origin.Name;
}

