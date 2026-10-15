namespace Stride.Editor.ImGui;

/// <summary>Native SelectionService Add policy over actual registered collections, without another selection model.</summary>
internal sealed class EditorNativeSelectionScopes
{
    private sealed class Scope(object owner, Func<string[]> capture, Action clear, Action? release)
    {
        internal readonly object Owner = owner;
        internal Func<string[]> Capture = capture;
        internal Action Clear = clear;
        internal object[] Collections = [owner];
        internal readonly Action? Release = release;
        internal HashSet<string> Observed = capture().ToHashSet(StringComparer.Ordinal);
    }
    private readonly Dictionary<string, Scope> scopes = new(StringComparer.Ordinal);
    private int suppressed;
    private bool clearing;
    internal void Register(string id, object owner, Func<string[]> capture, Action clear, Action? release = null, object[]? collections = null)
    {
        if (scopes.TryGetValue(id, out var previous))
        {
            if (ReferenceEquals(previous.Owner, owner))
            { previous.Capture = capture; previous.Clear = clear; previous.Collections = collections ?? [owner]; return; }
            scopes.Remove(id); previous.Release?.Invoke();
        }
        scopes.Add(id, new(owner, capture, clear, release) { Collections = collections ?? [owner] });
    }
    internal bool Contains(string id) => scopes.ContainsKey(id);
    internal string? FindCollection(object collection)
        => scopes.FirstOrDefault(pair => pair.Value.Collections.Any(value => ReferenceEquals(value, collection))).Key;
    internal void Retain(ISet<string> ids)
    {
        foreach (var id in scopes.Keys.Where(id => !ids.Contains(id)).ToArray())
        { var scope = scopes[id]; scopes.Remove(id); scope.Release?.Invoke(); }
    }
    internal void Changed(string id, bool collectionAdded = false, object? changedCollection = null)
    {
        if (!scopes.TryGetValue(id, out var source)) return;
        var current = source.Capture().ToHashSet(StringComparer.Ordinal);
        var added = collectionAdded && current.Count != 0 || current.Any(value => !source.Observed.Contains(value));
        source.Observed = current;
        if (!added || suppressed != 0 || clearing) return;
        clearing = true;
        try
        {
            foreach (var pair in scopes.ToArray())
                if (pair.Key != id && (changedCollection == null || !pair.Value.Collections.Any(value => ReferenceEquals(value, changedCollection)))
                    && pair.Value.Capture().Length != 0) pair.Value.Clear();
        }
        finally
        {
            clearing = false;
            foreach (var scope in scopes.Values.ToArray()) scope.Observed = scope.Capture().ToHashSet(StringComparer.Ordinal);
        }
    }
    internal IDisposable Suppress()
    { suppressed++; return new Suppression(this); }
    private sealed class Suppression(EditorNativeSelectionScopes owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (--owner.suppressed == 0)
                foreach (var scope in owner.scopes.Values.ToArray()) scope.Observed = scope.Capture().ToHashSet(StringComparer.Ordinal);
        }
    }
    internal void Clear()
    {
        foreach (var scope in scopes.Values) scope.Release?.Invoke();
        scopes.Clear();
    }
}
