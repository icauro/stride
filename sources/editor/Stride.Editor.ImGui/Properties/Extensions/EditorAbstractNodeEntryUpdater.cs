using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

// The original AbstractNodeEntryNodeUpdater rules on the canonical headless presenter.
internal sealed class EditorAbstractNodeEntryUpdater : NodePresenterUpdaterBase
{
    public override void UpdateNode(INodePresenter node)
    {
        var type = node.Descriptor.GetInnerCollectionType();
        if (!type.IsAbstract || TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NonInstantiableAttribute>(type) != null) return;
        IEnumerable<AbstractNodeEntry> entries = AbstractNodeType.GetInheritedInstantiableTypes(type);
        if (AssetRegistry.CanPropertyHandleContent(type, out var contentTypes))
            entries = entries.Where(entry => entry is not AbstractNodeType candidate || !contentTypes.Contains(candidate.Type));
        var member = node as MemberNodePresenter ?? (node as ItemNodePresenter)?.Parent as MemberNodePresenter;
        var collection = member?.MemberAttributes.OfType<MemberCollectionAttribute>().FirstOrDefault()
            ?? member?.Descriptor.Attributes.OfType<MemberCollectionAttribute>().FirstOrDefault();
        var allowsNull = member == null || (member.IsEnumerable && collection?.NotNullItems == true ? false : !member.MemberAttributes.OfType<NotNullAttribute>().Any());
        if (allowsNull) entries = new AbstractNodeEntry[] { AbstractNodeValue.Null }.Concat(entries);
        node.AttachedProperties.Set(AbstractNodeEntryData.Key, entries);
    }
}
