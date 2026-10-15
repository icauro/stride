namespace Stride.Editor.ImGui;

/// <summary>Retains an actual canonical graph generation during a native UI hierarchy gesture.</summary>
internal sealed class UIHierarchyDragOwner : IDisposable
{
    private IDisposable? lease;
    internal UIHierarchyDragOwner(EditorAuthoringState state)
    {
        State = state; Generation = state.Generation; Path = state.CanonicalPath;
        lease = state.AcquireView();
    }
    internal EditorAuthoringState State { get; }
    internal Guid Generation { get; }
    internal string Path { get; }
    internal bool IsCurrent => lease != null && State.IsPublished && !State.IsDisposed && State.Generation == Generation
        && StringComparer.OrdinalIgnoreCase.Equals(State.CanonicalPath, Path)
        && ReferenceEquals(State.Registry.Find(Path), State);
    public void Dispose() { var current = lease; lease = null; current?.Dispose(); }
}
