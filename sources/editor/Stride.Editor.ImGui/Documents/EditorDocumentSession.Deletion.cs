namespace Stride.Editor.ImGui;

internal sealed partial class EditorDocumentSession
{
    public int IndexOf(EditorDocumentEntry entry) => entries.IndexOf(entry);
    public void Detach(EditorDocumentEntry entry)
    {
        var index = entries.IndexOf(entry);
        if (index < 0) throw new InvalidOperationException("The document is no longer open.");
        entries.RemoveAt(index); paths.Remove(System.IO.Path.GetFullPath(entry.Path));
        if (entry.ViewLease != null) { entry.ViewLease.Dispose(); entry.ViewLease = null; }
        if (ReferenceEquals(Active, entry)) Active = entries.Count == 0 ? null : entries[Math.Min(index, entries.Count - 1)];
    }
    public void Restore(EditorDocumentEntry entry, int index)
    {
        var path = System.IO.Path.GetFullPath(entry.Path);
        if (entries.Contains(entry) || paths.ContainsKey(path))
            throw new InvalidOperationException("A document is already open at this path.");
        AttachLease(entry);
        paths.Add(path, entry);
        entries.Insert(Math.Clamp(index, 0, entries.Count), entry);
        Active ??= entry;
    }
}
