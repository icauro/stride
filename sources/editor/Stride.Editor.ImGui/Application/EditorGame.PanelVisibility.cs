namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ref bool showHierarchy => ref HierarchyState.Visible;
    private ref bool showInspector => ref PropertyGrid.Visible;
    private ref bool showAssets => ref AssetBrowserPanel.Visible;
    private ref bool showSolutionExplorer => ref SolutionExplorerController.Visible;
    private void RestorePanelVisibility(EditorPreferences current)
    {
        showHierarchy = current.ShowHierarchy; showInspector = current.ShowInspector;
        showAssets = current.ShowAssets; showOutput = current.ShowOutput;
        showSolutionExplorer = current.ShowSolutionExplorer;
        showAssetReferences = current.ShowReferences; showAssetErrors = current.ShowAssetErrors;
        showEditHistory = current.ShowEditHistory; showAssetPreview = current.ShowAssetPreview;
    }
    private void CapturePanelVisibility(EditorPreferences current)
    {
        current.ShowHierarchy = showHierarchy; current.ShowInspector = showInspector;
        current.ShowAssets = showAssets; current.ShowOutput = showOutput;
        current.ShowSolutionExplorer = showSolutionExplorer;
        current.ShowReferences = showAssetReferences; current.ShowAssetErrors = showAssetErrors;
        current.ShowEditHistory = showEditHistory; current.ShowAssetPreview = showAssetPreview;
    }
    private void ResetPanelVisibility()
    {
        showHierarchy = showInspector = showAssets = showOutput = true;
        showSolutionExplorer = true;
        showAssetReferences = showAssetErrors = showEditHistory = showAssetPreview = true;
    }
}
