using Stride.Core;

namespace Stride.Editor.ImGui;

internal sealed record CompositorPropertyGroup<T>(string Name, ExpandRule Expand, IReadOnlyList<T> Fields);

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<IReadOnlyList<CompositorProperty>, IReadOnlyList<CompositorPropertyGroup<CompositorProperty>>> compositorPropertyGroups = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IReadOnlyList<CompositorAggregateProperty>, IReadOnlyList<CompositorPropertyGroup<CompositorAggregateProperty>>> compositorAggregateGroups = new(ReferenceEqualityComparer.Instance);
    public IReadOnlyList<CompositorPropertyGroup<CompositorProperty>> GetCompositorPropertyGroups(IReadOnlyList<CompositorProperty> properties)
    {
        if (!compositorPropertyGroups.TryGetValue(properties, out var groups))
            compositorPropertyGroups.Add(properties, groups = BuildCompositorGroups(properties, static field => field));
        return groups;
    }
    public IReadOnlyList<CompositorPropertyGroup<CompositorAggregateProperty>> GetCompositorAggregateGroups(IReadOnlyList<CompositorAggregateProperty> properties)
    {
        if (!compositorAggregateGroups.TryGetValue(properties, out var groups))
            compositorAggregateGroups.Add(properties, groups = BuildCompositorGroups(properties.Where(field => field.Fields.Any(IsCompositorPropertyVisible)).ToArray(), static field => field.First));
        return groups;
    }
    private static IReadOnlyList<CompositorPropertyGroup<T>> BuildCompositorGroups<T>(IReadOnlyList<T> fields, Func<T, CompositorProperty> describe)
        => fields.GroupBy(field => describe(field).Category).OrderBy(group => describe(group.First()).NativeCategoryOrder ?? describe(group.First()).CategoryMetadata?.Order ?? group.Min(field => describe(field).Order))
            .Select(group => new CompositorPropertyGroup<T>(group.Key, describe(group.First()).NativeCategoryExpansion ?? describe(group.First()).CategoryMetadata?.Expand ?? ExpandRule.Always,
                group.OrderBy(field => describe(field).Order).ToArray())).ToArray();
}
