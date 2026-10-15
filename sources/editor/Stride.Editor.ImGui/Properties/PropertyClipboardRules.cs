using Stride.Core.Annotations;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal static class PropertyClipboardRules
{
    internal static bool IsReadOnly(IMemberNode member)
    {
        var registry = TypeDescriptorFactory.Default.AttributeRegistry;
        return registry.GetAttribute<MemberCollectionAttribute>(member.MemberDescriptor.MemberInfo)?.ReadOnly == true
            || registry.GetAttribute<MemberCollectionAttribute>(member.Type)?.ReadOnly == true;
    }
    internal static bool CanAdd(IObjectNode collection) => NativePropertyClipboard.HasAdd(collection);
    internal static bool CanRemove(IObjectNode collection) => NativePropertyClipboard.HasRemove(collection);
    internal static bool NotNullItems(IMemberNode member)
    {
        var registry = TypeDescriptorFactory.Default.AttributeRegistry;
        return registry.GetAttribute<MemberCollectionAttribute>(member.MemberDescriptor.MemberInfo)?.NotNullItems == true
            || registry.GetAttribute<MemberCollectionAttribute>(member.Type)?.NotNullItems == true;
    }
    internal static Type? ElementType(IObjectNode collection) => collection.Descriptor switch
    {
        DictionaryDescriptor dictionary => dictionary.ValueType,
        CollectionBaseDescriptor descriptor => descriptor.ValueType,
        _ => null,
    };
}
