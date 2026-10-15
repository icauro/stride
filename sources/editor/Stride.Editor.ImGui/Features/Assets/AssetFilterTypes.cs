using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>The native filter identity is the registered asset's full type name.</summary>
internal static class AssetFilterTypes
{
    public static IEqualityComparer<Type> Comparer { get; } = new FullNameComparer();

    public static bool MatchesPreference(Type type, string value) =>
        string.Equals(type.FullName, value, StringComparison.Ordinal)
        || type.FullName != null && value.StartsWith(type.FullName + ",", StringComparison.Ordinal);

    public static Type[] Refresh(HashSet<Type> selected, HashSet<Type> disabled, IEnumerable<Type>? liveTypes)
    {
        // Prefer the canonical session type over an older registry generation.
        var available = (liveTypes ?? []).Concat(AssetRegistry.GetPublicTypes())
            .Where(type => type != typeof(Package) && typeof(Asset).IsAssignableFrom(type))
            .Distinct(Comparer).OrderBy(type => Stride.Core.DisplayAttribute.GetDisplayName(type), StringComparer.OrdinalIgnoreCase)
            .ThenBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        Rebind(selected, available); Rebind(disabled, available);
        return available;
    }

    private static void Rebind(HashSet<Type> filters, Type[] available)
    {
        foreach (var type in available)
            if (filters.Remove(type)) filters.Add(type);
    }

    private sealed class FullNameComparer : IEqualityComparer<Type>
    {
        public bool Equals(Type? left, Type? right) => ReferenceEquals(left, right)
            || left != null && right != null && string.Equals(left.FullName ?? left.Name, right.FullName ?? right.Name, StringComparison.Ordinal);
        public int GetHashCode(Type type) => StringComparer.Ordinal.GetHashCode(type.FullName ?? type.Name);
    }
}
