namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool assetTileView { get => AssetBrowserPanel.TileView; set => AssetBrowserPanel.TileView = value; }
    private int assetTileThumbnailSize { get => AssetBrowserPanel.ThumbnailSize; set => AssetBrowserPanel.ThumbnailSize = value; }
    private void SaveAssetViewPreferences(bool saveThumbnail = false)
    {
        if (preferences == null) return;
        var previousView = preferences.Current.AssetTileView;
        var previousSize = preferences.Current.AssetTileThumbnailSize;
        var previousGridSize = preferences.Current.AssetGridThumbnailSize;
        preferences.Current.AssetTileView = assetTileView;
        if (saveThumbnail)
        {
            if (assetTileView) preferences.Current.AssetTileThumbnailSize = assetTileThumbnailSize;
            else preferences.Current.AssetGridThumbnailSize = AssetBrowserPanel.GridThumbnailSize;
        }
        if (preferences.Save()) return;
        assetTileView = preferences.Current.AssetTileView = previousView;
        assetTileThumbnailSize = preferences.Current.AssetTileThumbnailSize = previousSize;
        AssetBrowserPanel.GridThumbnailSize = preferences.Current.AssetGridThumbnailSize = previousGridSize;
        ReportAssetPreferenceSaveError();
    }
}
