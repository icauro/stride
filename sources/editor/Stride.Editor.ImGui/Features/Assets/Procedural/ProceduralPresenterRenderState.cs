using System.Runtime.CompilerServices;

namespace Stride.Editor.ImGui;

internal sealed class ProceduralPresenterRenderState
{
    private sealed record Frame(int Number, NativePresenterCategories Categories)
    {
        internal readonly HashSet<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter> Consumed = [];
    }
    private readonly ConditionalWeakTable<ProceduralAssetDocument, Frame> owners = new();
    internal NativePresenterCategories Categories(ProceduralAssetDocument owner, int frame,
        Func<NativePresenterCategories> create)
    {
        if (owners.TryGetValue(owner, out var state) && state.Number == frame) return state.Categories;
        owners.Remove(owner);
        var next = new Frame(frame, create()); owners.Add(owner, next); return next.Categories;
    }
    internal bool Consume(ProceduralAssetDocument owner, Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter presenter)
        => owners.TryGetValue(owner, out var frame) && frame.Consumed.Add(presenter);
}
