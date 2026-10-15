using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private void ApplyCompositorNativeMetadata(CompositorProperty property)
    {
        if (CompositorNumericPresenter(property) is not { } presenter) return;
        if (presenter.DisplayName != presenter.Name) property.Label = presenter.DisplayName;
        property.Order = presenter.Order ?? property.Order;
        var presentation = property.NativePresenter is { } native ? ResolveExtensionPresentation(native)
            : property.Accessor.Node is IMemberNode member ? ResolveExtensionPresentation(member)
            : property.Accessor.Node is IObjectNode collection ? ResolveExtensionPresentation(collection) : null;
        if (presentation?.NumericRange(presenter) is { } range)
        {
            property.Range = range;
            property.UsesRangedNumericTemplate = NativeRangedNumericControl.Matches(property.DeclaredType, range);
        }
        if (presenter.AttachedProperties.TryGetValue(InlineData.InlineMemberKey, out var isInline) && isInline
            && presenter.TryGetChild(InlineData.InlinedProperty)?.GetNodeAccessor().Node is IMemberNode inlineMember
            && property.Children.FirstOrDefault(child => ReferenceEquals(child.Accessor.Node, inlineMember)) is { } inline)
        {
            property.InlineMember = inline; inline.Label = property.Label; inline.PresentationOwner = property;
            property.Details = property.Children.Where(child => !ReferenceEquals(child, inline) && !ReferenceEquals(child, property.EnabledMember)).ToArray();
        }
        if (presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule)) property.Expansion = rule;
        for (var parent = presenter.Parent; parent != null; parent = parent.Parent)
        {
            if (!NativePresenterCategories.IsCategory(parent)) continue;
            property.Category = parent.DisplayName;
            property.NativeCategoryOrder = parent.Order;
            if (parent.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var expansion))
                property.NativeCategoryExpansion = expansion;
            break;
        }
    }
    internal EditorPresenterTarget? AcquireCompositorRootPresenter(IObjectNode root)
        => ResolveExtensionPresentation(root) is { } presentation
            && EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, root) is { } presenter
                ? presentation.AcquireTarget(presenter) : null;
    internal bool IsCompositorPropertyVisible(CompositorProperty property)
        => CompositorNumericPresenter(property) is not { } presenter || EditorPresenterItemMetadata.IsVisible(presenter);
    internal void TransformCompositorVirtualProperties(IReadOnlyList<VirtualNodePresenter> presenters, Func<int, object?, object?> transform)
    {
        CommitEdits();
        if (presenters.Count == 0 || presenters.Any(presenter => !ExtensionVirtualProperties.Contains(presenter)))
            throw new InvalidOperationException("The native compositor virtual property changed.");
        var values = presenters.Select((presenter, index) => NormalizeExtensionVirtualValue(presenter, transform(index, presenter.Value))).ToArray();
        var aliases = new Dictionary<NodeAccessor, object?>();
        for (var index = 0; index < presenters.Count; index++)
        {
            var accessor = presenters[index].GetNodeAccessor();
            if (aliases.TryGetValue(accessor, out var previous) && !ContentAssetReference.AreEqual(previous, values[index]))
                throw new InvalidOperationException("Aliases of the same compositor virtual property cannot receive different values.");
            aliases[accessor] = values[index];
        }
        if (presenters.Select((presenter, index) => ContentAssetReference.AreEqual(presenter.Value, values[index])).All(equal => equal)) return;
        ApplyInheritedAssetMutation(() =>
        {
            var applied = new HashSet<NodeAccessor>();
            for (var index = 0; index < presenters.Count; index++)
                if (applied.Add(presenters[index].GetNodeAccessor())) presenters[index].UpdateValue(values[index]!);
        }, "Edit " + presenters[0].DisplayName);
    }
}
