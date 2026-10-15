using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private INodePresenter? ImportItemNumericPresenter(AssetImportSettingItem item)
        => FindExtensionItemPresenter(item.Collection, item.Index);
    internal bool IsImportItemPresentationReadOnly(AssetImportSettingItem item)
        => ImportItemNumericPresenter(item) is { } presenter
        && (EditorPresenterItemMetadata.IsReadOnly(presenter) || EditorPresenterItemMetadata.HasReadOnlyItemAncestor(presenter));
    internal PropertyNumericRange? ImportItemNumericRange(AssetImportSettingItem item)
        => ImportItemNumericPresenter(item) is { } presenter
            ? ResolveExtensionPresentation(item.Collection)?.NumericRange(presenter) : null;
    internal IReadOnlyList<PropertyNumericRange?> ImportItemAxisNumericRanges(AssetImportSettingItem item)
    {
        var fallback = VectorPropertyNumericMetadata.Create(item.Type);
        return ImportItemNumericPresenter(item) is { } presenter && ResolveExtensionPresentation(item.Collection) is { } presentation
            ? presentation.NumericAxisRanges(presenter, fallback) : fallback;
    }
    internal bool IsImportItemAxisReadOnly(AssetImportSettingItem item, int axis)
        => ImportItemNumericPresenter(item) is { } presenter && NativeNumericAxes.IsReadOnly(presenter, axis);
    private void AssertImportItemAxesEditable(AssetImportSettingItem item, object? previous, object? proposed)
    {
        // RotationEditor and ColorPicker author the actual whole parent value.
        if (item.Type != typeof(Stride.Core.Mathematics.Quaternion) && ImportItemNumericPresenter(item) is { } presenter)
            NativeNumericAxes.AssertEditable(presenter, previous, proposed);
    }
}
