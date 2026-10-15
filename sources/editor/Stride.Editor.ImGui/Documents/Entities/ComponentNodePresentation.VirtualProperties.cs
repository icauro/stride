using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class ComponentNodePresentation
{
    internal INodePresenter RootPresenter => root;
    internal object AuthoredValue => authoredRoot.Retrieve()!;
    internal IReadOnlyList<VirtualNodePresenter> VirtualRows
    {
        get { RebuildMap(); return observed.OfType<VirtualNodePresenter>().Where(node =>
            { for (INodePresenter? current = node; current != null; current = current.Parent) if (!current.IsVisible) return false; return true; }).ToArray(); }
    }
    internal bool OwnsVirtual(VirtualNodePresenter presenter) { RebuildMap(); return !disposed && !disposeRequested && observed.Contains(presenter); }
    internal IReadOnlyList<PropertyNumericRange?> VirtualAxisNumericRanges(VirtualNodePresenter presenter)
        => ReadAxisNumericRanges(presenter, presenter.Type, Array.Empty<PropertyNumericRange?>());
    internal PropertyNumericRange? VirtualNumericRange(VirtualNodePresenter presenter) => ReadItemNumericRange(presenter, presenter.Type);
    internal EditorPresenterTarget AcquireVirtualTarget(VirtualNodePresenter presenter)
    {
        if (!OwnsVirtual(presenter)) throw new InvalidOperationException("The component presenter is no longer active.");
        uses++; var released = false;
        return new(presenter, () => { if (!released) { uses--; released = true; } if (uses == 0 && disposeRequested) Dispose(); });
    }
}
