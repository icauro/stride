using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    protected override INodePresenter? FindExtensionItemPresenter(IObjectNode collection, NodeIndex index)
        => elementPresentations.Values.Select(presentation => EditorPresenterItemMetadata.Find(presentation.RootPresenter, collection, index))
            .FirstOrDefault(presenter => presenter != null);
}
