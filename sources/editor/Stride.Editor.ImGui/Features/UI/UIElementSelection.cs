using System.Collections;

namespace Stride.Editor.ImGui;

/// <summary>Actual UI editor selection order; native LastSelectedElement follows the last remaining item.</summary>
internal sealed class UIElementSelection : IReadOnlyCollection<Guid>
{
    private readonly List<Guid> ordered = [];
    private readonly HashSet<Guid> members = [];
    internal Action? Added;
    public int Count => ordered.Count;
    internal bool Contains(Guid id) => members.Contains(id);
    internal bool SetEquals(IEnumerable<Guid> values) => members.SetEquals(values);
    internal bool Add(Guid id)
    {
        if (!members.Add(id)) return false;
        ordered.Add(id); Added?.Invoke(); return true;
    }
    internal bool Remove(Guid id)
    {
        if (!members.Remove(id)) return false;
        ordered.Remove(id); return true;
    }
    internal void Clear() { ordered.Clear(); members.Clear(); }
    internal void UnionWith(IEnumerable<Guid> values) { foreach (var id in values) Add(id); }
    internal int RemoveWhere(Predicate<Guid> predicate)
    {
        var removed = ordered.Where(id => predicate(id)).ToArray();
        foreach (var id in removed) Remove(id);
        return removed.Length;
    }
    public IEnumerator<Guid> GetEnumerator() => ordered.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
