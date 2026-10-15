namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ClearSelectedAssetPreview()
    {
        // WPF changes to a null factory result after disposing the previous preview.
        // The authoring documents remain canonical and are not closed by selection.
        CloseCatalogAssetPreviews();
        CloseLegacyAssetPreviews();
        assetPreviewPath = null;
        focusAssetPreview = false;
    }
}
