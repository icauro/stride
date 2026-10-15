namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EngineViewport? highlightedAssetViewport;
    private long highlightedAssetSelection = -1;
    private void UpdateSceneSelectedAssetHighlights()
    {
        if (workspace == null || engineViewport == null) return;
        if (!ReferenceEquals(highlightedAssetViewport, engineViewport) || highlightedAssetSelection != assetBrowser.SelectionVersion)
        {
            assetReferences.Update(workspace, documents, assetBrowser);
            var items = assetBrowser.SelectedEntries.Select(entry => workspace.Session.FindAsset(entry.Id)).OfType<Stride.Core.Assets.AssetItem>().ToArray();
            engineViewport.HighlightSelectedAssets(items, assetReferences.DependencySnapshot);
            highlightedAssetViewport = engineViewport; highlightedAssetSelection = assetBrowser.SelectionVersion;
        }
        engineViewport.UpdateSelectedAssetHighlights();
    }
}
