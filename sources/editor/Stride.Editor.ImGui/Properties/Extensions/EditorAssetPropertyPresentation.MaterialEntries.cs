using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorAssetPropertyPresentation
{
    internal IEnumerable<AbstractNodeEntry>? AbstractEntries(IMemberNode member)
    {
        var presenter = Find(member);
        return presenter != null && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries : null;
    }
}
