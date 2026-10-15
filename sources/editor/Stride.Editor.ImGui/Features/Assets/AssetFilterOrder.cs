namespace Stride.Editor.ImGui;

/// <summary>Original mixed-category collection order without retaining assembly generations.</summary>
internal sealed class AssetFilterOrder
{
    private readonly List<(string Category, string Value)> order = [];
    private static bool Same((string Category, string Value) key, string category, string value) => key.Category == category
        && string.Equals(key.Value, value, category == "Type" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    internal void Remember(string category, string value)
    { order.RemoveAll(key => Same(key, category, value)); order.Insert(0, (category, value)); }
    internal void Append(string category, string value)
    { if (!order.Any(key => Same(key, category, value))) order.Add((category, value)); }
    internal void Remove(string category, string value) => order.RemoveAll(key => Same(key, category, value));
    internal void RemoveCategory(string category) => order.RemoveAll(key => key.Category == category);
    internal void Clear() => order.Clear();
    internal AssetFilterPreference[] Arrange(IEnumerable<AssetFilterPreference> current)
    {
        var filters = current.ToArray();
        var keys = filters.Select(filter => (filter.Category, filter.Value)).ToHashSet(KeyComparer.Instance);
        order.RemoveAll(key => !keys.Contains(key));
        var positions = new Dictionary<(string Category, string Value), int>(KeyComparer.Instance);
        for (var index = 0; index < order.Count; index++) positions.TryAdd(order[index], index);
        foreach (var filter in filters)
            if (positions.TryAdd((filter.Category, filter.Value), order.Count)) order.Add((filter.Category, filter.Value));
        return filters.OrderBy(filter => positions[(filter.Category, filter.Value)]).ToArray();
    }
    private sealed class KeyComparer : IEqualityComparer<(string Category, string Value)>
    {
        internal static readonly KeyComparer Instance = new();
        public bool Equals((string Category, string Value) left, (string Category, string Value) right) => Same(left, right.Category, right.Value);
        public int GetHashCode((string Category, string Value) key) => HashCode.Combine(key.Category,
            (key.Category == "Type" ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).GetHashCode(key.Value));
    }
}
