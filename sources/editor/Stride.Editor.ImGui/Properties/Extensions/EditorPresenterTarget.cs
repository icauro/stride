using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

/// <summary>Retains the actual presenter trees/provider generations across asynchronous command execution.</summary>
internal sealed class EditorPresenterTarget : IDisposable
{
    private readonly Action release;
    private bool disposed;
    internal EditorPresenterTarget(INodePresenter presenter, Action release)
    { Presenters = [presenter]; this.release = release; }
    internal EditorPresenterTarget(IReadOnlyList<EditorPresenterTarget> targets)
    { Presenters = targets.SelectMany(target => target.Presenters).ToArray(); release = () => { foreach (var target in targets) target.Dispose(); }; }
    internal IReadOnlyList<INodePresenter> Presenters { get; }
    internal INodePresenter Presenter => Presenters[0];
    public void Dispose() { if (disposed) return; release(); disposed = true; }
}
