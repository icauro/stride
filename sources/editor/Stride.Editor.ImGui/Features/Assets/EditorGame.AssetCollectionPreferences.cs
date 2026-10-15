namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly AssetSortMetadata assetSortMetadata = new();
    private void UpdateAssetSortMetadata()
    {
        if (workspace == null || assetBrowser.Sort is not (AssetBrowserSort.DirtyThenName or AssetBrowserSort.ModificationDateThenName)
            && assetReferences.Sort is not (AssetBrowserSort.DirtyThenName or AssetBrowserSort.ModificationDateThenName)) return;
        assetSortMetadata.Update(workspace, documents);
        assetBrowser.UpdateSortMetadata(assetSortMetadata);
        assetReferences.UpdateSortMetadata(assetSortMetadata);
    }
    private void SaveAssetCollectionPreferences()
    {
        UpdateAssetSortMetadata();
        if (preferences == null) return;
        var previousSort = preferences.Current.AssetSort;
        var previousDescendants = preferences.Current.AssetIncludeDescendants;
        var previousDisplay = preferences.Current.AssetDisplayMode;
        preferences.Current.AssetSort = assetBrowser.Sort;
        preferences.Current.AssetIncludeDescendants = assetBrowser.IncludeDescendants;
        preferences.Current.AssetDisplayMode = assetBrowser.DisplayMode;
        if (preferences.Save()) return;
        assetBrowser.Sort = preferences.Current.AssetSort = previousSort;
        assetBrowser.IncludeDescendants = preferences.Current.AssetIncludeDescendants = previousDescendants;
        preferences.Current.AssetDisplayMode = previousDisplay;
        assetBrowser.DisplayMode = previousDisplay ?? (previousDescendants ? AssetBrowserDisplayMode.RecursiveAssets : AssetBrowserDisplayMode.AssetsAndFolders);
        ReportAssetPreferenceSaveError();
    }
    private void ZoomReferenceThumbnails(int direction)
    {
        var size = referenceTileView ? referenceThumbnailSize : referenceGridThumbnailSize;
        var next = AssetBrowserZoomGesture.NextSize(size, direction);
        if (next == size) return;
        if (referenceTileView) referenceThumbnailSize = next;
        else referenceGridThumbnailSize = next;
        SaveReferenceZoom();
    }

    private void SaveReferenceZoom()
    {
        if (preferences == null) return;
        var previous = referenceTileView ? preferences.Current.AssetTileThumbnailSize : preferences.Current.AssetGridThumbnailSize;
        if (referenceTileView) preferences.Current.AssetTileThumbnailSize = referenceThumbnailSize;
        else preferences.Current.AssetGridThumbnailSize = referenceGridThumbnailSize;
        if (preferences.Save()) return;
        if (referenceTileView) referenceThumbnailSize = preferences.Current.AssetTileThumbnailSize = previous;
        else referenceGridThumbnailSize = preferences.Current.AssetGridThumbnailSize = previous;
        ReportAssetPreferenceSaveError();
    }
    private void ReportAssetPreferenceSaveError()
    {
        statusMessage = preferences?.Error ?? "Unable to save editor preferences.";
        outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Editor preferences", statusMessage);
    }
    private int SaveAssetPickerThumbnailSize(int size)
    {
        size = Math.Clamp(size, 16, 128);
        if (preferences == null) return size;
        var previous = preferences.Current.AssetTileThumbnailSize;
        if (previous == size) return size;
        preferences.Current.AssetTileThumbnailSize = size;
        if (preferences.Save()) return size;
        preferences.Current.AssetTileThumbnailSize = previous; ReportAssetPreferenceSaveError(); return previous;
    }
}
