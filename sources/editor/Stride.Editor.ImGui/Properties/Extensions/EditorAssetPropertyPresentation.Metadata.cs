using System.Runtime.CompilerServices;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorAssetPropertyPresentation
{
    private sealed record NumericMetadata(object? Minimum, object? Maximum, object? Small, object? Large, object? Places,
        PropertyNumericRange Range);
    private readonly ConditionalWeakTable<INodePresenter, NumericMetadata> numericMetadata = new();
    private readonly ConditionalWeakTable<INodePresenter, NumericAxisMetadata> numericAxes = new();
    private sealed record NumericAxisMetadata(IReadOnlyList<PropertyNumericRange?> Ranges);
    internal IReadOnlyList<PropertyNumericRange?> NumericAxisRanges(INodePresenter presenter, IReadOnlyList<PropertyNumericRange?> fallback)
    {
        var names = NativeNumericAxes.Names(presenter.Type);
        if (names.Count == 0) return [];
        PropertyNumericRange? Read(int axis) => presenter.TryGetChild(names[axis]) is { } child
            ? NumericRange(child) ?? fallback.ElementAtOrDefault(axis) : fallback.ElementAtOrDefault(axis);
        if (numericAxes.TryGetValue(presenter, out var cached) && cached.Ranges.Count == names.Count
            && Enumerable.Range(0, names.Count).All(axis => ReferenceEquals(cached.Ranges[axis], Read(axis)))) return cached.Ranges;
        var ranges = Enumerable.Range(0, names.Count).Select(Read).ToArray();
        numericAxes.Remove(presenter); numericAxes.Add(presenter, new(ranges)); return ranges;
    }
    internal PropertyNumericRange? NumericRange(IMemberNode member)
    {
        return Find(member) is { } presenter ? NumericRange(presenter) : null;
    }
    internal PropertyNumericRange? NumericRange(INodePresenter presenter)
    {
        var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
        if (!AssetSettingNumericMetadata.Supports(type)) return null;
        var properties = presenter.AttachedProperties;
        properties.TryGetValue(NumericData.MinimumKey, out var minimum);
        properties.TryGetValue(NumericData.MaximumKey, out var maximum);
        properties.TryGetValue(NumericData.SmallStepKey, out var small);
        properties.TryGetValue(NumericData.LargeStepKey, out var large);
        properties.TryGetValue(NumericData.DecimalPlacesKey, out var places);
        if (minimum == null && maximum == null && small == null && large == null && places == null) return null;
        if (numericMetadata.TryGetValue(presenter, out var cached) && Equals(cached.Minimum, minimum)
            && Equals(cached.Maximum, maximum) && Equals(cached.Small, small) && Equals(cached.Large, large) && Equals(cached.Places, places))
            return cached.Range;
        var range = new PropertyNumericRange(new PropertyNumericMetadata(minimum == null ? null : Convert.ToDouble(minimum),
            maximum == null ? null : Convert.ToDouble(maximum), small == null ? null : Convert.ToDouble(small),
            large == null ? null : Convert.ToDouble(large), places == null ? null : Convert.ToInt32(places)), type, places != null);
        numericMetadata.Remove(presenter); numericMetadata.Add(presenter, new(minimum, maximum, small, large, places, range));
        return range;
    }
    internal IEnumerable<string>? ShaderNames(IMemberNode member)
    {
        var presenter = Find(member);
        return presenter != null && presenter.AttachedProperties.TryGetValue(Stride.Assets.Presentation.NodePresenters.Keys.MaterialData.Key, out var names) ? names : null;
    }
    internal int? Order(IMemberNode member) => Find(member)?.Order;
    internal ExpandRule Expansion(IMemberNode member)
    {
        var presenter = Find(member);
        return presenter != null && presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var expand) ? expand : ExpandRule.Auto;
    }
    internal IMemberNode? InlineMember(IMemberNode member)
    {
        var presenter = Find(member);
        if (presenter == null || !presenter.AttachedProperties.TryGetValue(InlineData.InlineMemberKey, out var inline) || !inline) return null;
        return presenter.TryGetChild(InlineData.InlinedProperty)?.GetNodeAccessor().Node as IMemberNode;
    }
    internal INodePresenter? ItemPresenter(IObjectNode collection, NodeIndex index)
        => EditorPresenterItemMetadata.Find(RootPresenter, collection, index);
}
