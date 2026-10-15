using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;

namespace Stride.Editor.ImGui;

internal static class NativeAbstractEntries
{
    internal static IReadOnlyList<AbstractNodeEntry> Available(IEnumerable<AbstractNodeEntry>? supplied, IEnumerable<Type> fallback, bool allowsNull, bool allowNullWithoutTypes = false)
    {
        if (supplied != null) return supplied.OrderBy(entry => entry.Order).ToArray();
        IEnumerable<AbstractNodeEntry> entries = fallback.Select(type => (AbstractNodeEntry)new AbstractNodeType(type));
        if (allowsNull && (allowNullWithoutTypes || entries.Any())) entries = new AbstractNodeEntry[] { AbstractNodeValue.Null }.Concat(entries);
        return entries.ToArray();
    }
    internal static IReadOnlyList<AbstractNodeEntry> Combine(IReadOnlyList<IReadOnlyList<AbstractNodeEntry>> sets)
        => sets.Count == 0 ? [] : Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys.AbstractNodeEntryData.CombineProperties<AbstractNodeEntry>(sets.Cast<object>()).ToArray();
    internal static AbstractNodeEntry? TypeEntry(IEnumerable<AbstractNodeEntry> entries, Type? type)
    {
        foreach (var entry in entries)
        {
            if (type == null && entry is AbstractNodeValue value && value.Value == null) return entry;
            if (entry is AbstractNodeType candidate && candidate.Type == type) return entry;
            if (entry is AbstractNodeTypeGroup group && group.Types.FirstOrDefault(candidate => candidate.Type == type) is { } child) return child;
        }
        return null;
    }
    internal static bool Accepts(IEnumerable<AbstractNodeEntry>? supplied, AbstractNodeEntry entry) => supplied == null || Contains(supplied, entry);
    internal static bool Contains(IEnumerable<AbstractNodeEntry> entries, AbstractNodeEntry entry)
        => entry is not AbstractNodeTypeGroup && entries.Any(candidate => candidate.Equals(entry)
            || candidate is AbstractNodeTypeGroup group && group.Types.Any(type => type.Equals(entry)));
}
