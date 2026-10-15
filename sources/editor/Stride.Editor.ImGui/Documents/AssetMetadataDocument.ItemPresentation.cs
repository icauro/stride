using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    protected virtual INodePresenter? FindExtensionItemPresenter(IObjectNode collection, NodeIndex index)
        => ResolveExtensionPresentation(collection) is { } presentation ? EditorPresenterItemMetadata.Find(presentation.RootPresenter, collection, index) : null;
    internal bool IsExtensionItemVisible(IObjectNode collection, NodeIndex index)
        => FindExtensionItemPresenter(collection, index) is not { } presenter || EditorPresenterItemMetadata.IsVisible(presenter);
    internal bool IsExtensionItemReadOnly(IObjectNode collection, NodeIndex index)
        => FindExtensionItemPresenter(collection, index) is { } presenter && EditorPresenterItemMetadata.IsReadOnly(presenter);
    internal string? ExtensionItemLabel(IObjectNode collection, NodeIndex index)
        => FindExtensionItemPresenter(collection, index) is { } presenter && presenter.DisplayName != presenter.Name ? presenter.DisplayName : null;
}
