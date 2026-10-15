using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record ProceduralCollectionContext(AssetPropertyField Origin, IAssetObjectNode Node, ProceduralCollectionItem? Parent = null)
{
    internal Type ElementType => Node.Descriptor switch
    {
        ArrayDescriptor array => array.ElementType,
        CollectionDescriptor collection => collection.ElementType,
        DictionaryDescriptor dictionary => dictionary.ValueType,
        _ => throw new InvalidOperationException("The native node is not a supported collection.")
    };
    internal MemberCollectionAttribute? Rules => Parent == null
        ? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Origin.Node.MemberDescriptor.MemberInfo)
        : TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Node.Type);
    internal string Key => Parent?.Key ?? Origin.Name;
}

internal sealed record ProceduralCollectionItem(ProceduralCollectionContext Context, NodeIndex OriginalIndex, ItemId? Id)
{
    internal NodeIndex Index => Id is { } id ? Context.Node.IdToIndex(id) : OriginalIndex;
    internal object? Value => Context.Node.Retrieve(Index);
    internal Type DeclaredType => Context.ElementType;
    internal Type Type => Nullable.GetUnderlyingType(DeclaredType) ?? DeclaredType;
    internal bool AllowsNull => Context.Rules?.NotNullItems != true && (!DeclaredType.IsValueType || Nullable.GetUnderlyingType(DeclaredType) != null);
    internal string Key => Context.Key + "/" + (Id?.ToString() ?? OriginalIndex.ToString());
}
