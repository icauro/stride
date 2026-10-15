using Stride.Core;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal sealed record PresentedFontItem(FontSettingItem Item, string Label, int? Order, string Name, ExpandRule Expansion);

    internal IReadOnlyList<PresentedFontItem> PresentedFontItems(FontSettingField member)
    {
        var rows = new List<PresentedFontItem>();
        foreach (var item in member.Items)
        {
            if (!TryGetFontItemAccessor(member, item, out var accessor)) continue;
            var presenter = FindExtensionItemPresenter((Stride.Core.Quantum.IObjectNode)accessor.Node, accessor.Index);
            if (presenter != null && !EditorPresenterItemMetadata.IsVisible(presenter)) continue;
            var expansion = presenter != null && presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule)
                ? rule : ExpandRule.Auto;
            rows.Add(new(item, presenter?.DisplayName ?? accessor.Index.ToString(), presenter?.Order,
                presenter?.Name ?? accessor.Index.ToString(), expansion));
        }
        // Stock NodeViewModel gives explicit Order priority and compares names only when both orders are absent.
        return rows.OrderBy(row => row, Comparer<PresentedFontItem>.Create((left, right) =>
        {
            var order = (left.Order ?? 0).CompareTo(right.Order ?? 0);
            if (order != 0) return order;
            if (left.Order == null && right.Order == null)
                return StringComparer.InvariantCultureIgnoreCase.Compare(left.Name, right.Name);
            return left.Order == null ? 1 : right.Order == null ? -1 : 0;
        })).ToArray();
    }
}
