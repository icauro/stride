namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal EditorPresenterTarget? AcquireImportItemCommandTarget(AssetImportSettingItem item)
    {
        if (!CanCopyImportItem(item)) return null;
        var index = item.Index;
        var value = item.Value;
        var presentation = ExtensionAssetPresentation;
        var presenter = presentation?.ItemPresenter(item.Collection, index);
        if (presenter == null || !CanCopyImportItem(item) || item.Index != index
            || !ContentAssetReference.AreEqual(item.Value, value)) return null;
        return presentation!.AcquireTarget(presenter);
    }
}
