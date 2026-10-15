using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record UIPropertyCollection(Guid ElementId, IMemberNode Member, IAssetObjectNode Collection, string Path, string Label, PropertyKey? AttachedKey = null, bool AttachedRoot = false)
{
    internal Type ElementType => PropertyClipboardRules.ElementType(Collection) ?? throw new InvalidOperationException("The collection has no element descriptor.");
    internal NodeAccessor Accessor => !AttachedRoot ? new(Member, NodeIndex.Empty) : new(Member.Target!, new NodeIndex(AttachedKey));
    internal MemberCollectionAttribute? Rules => (!AttachedRoot ? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Member.MemberDescriptor.MemberInfo) : null)
        ?? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Collection.Type);
    internal IReadOnlyList<UIPropertyCollectionItem> Items => (Collection.Indices ?? []).Select(index =>
        new UIPropertyCollectionItem(this, Collection.TryIndexToId(index, out var id) ? id : null, index)).ToArray();
}
internal sealed record UIPropertyCollectionItem(UIPropertyCollection Owner, ItemId? Id, NodeIndex OriginalIndex)
{
    internal NodeIndex Index => Id is { } id ? Owner.Collection.IdToIndex(id) : OriginalIndex;
    internal object? Value => Owner.Collection.Retrieve(Index);
    internal string Identity => Id?.ToString() ?? OriginalIndex.ToString();
}
