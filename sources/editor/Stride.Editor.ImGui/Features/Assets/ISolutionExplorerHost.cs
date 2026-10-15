using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal interface ISolutionExplorerHost
{
    bool HasWorkspace { get; }
    void RecordLocationControl(string id, bool open) { }
    ProjectWorkspace? Workspace { get; }
    void ResetWorkspaceTracking();
    bool DrawExpansionButton(string icon, string tooltip);
    bool DrawLocationToolbarButton(string icon, string tooltip, bool enabled);
    Action? CaptureLocationDeletion();
    Action? CaptureLocationExplore();
    AssetBrowserState Browser { get; }
    void RefreshFolders();
    void SelectPackageProperties(Package package);
    void OnLocationSelectionChanged();
    Action? DrawCategoryActions(AssetBrowserCategory category);
    Action? DrawAssetAdditionAction();
    Action? DrawProjectActions(AssetBrowserLocation location);
    void DrawFolderDropTarget(AssetBrowserFolder folder);
    void DrawDependencyActions(Package package);
    bool CanCreateFolder(AssetBrowserFolder folder);
    void RequestFolderCreation(AssetBrowserFolder folder);
    void DrawDependencies(Package package);
    Action? DrawFolderClipboardActions(AssetBrowserFolder folder);
    void Execute(Action action);
    bool CanRenameFolder(AssetBrowserFolder folder);
    bool CanDeleteFolder(AssetBrowserFolder folder);
    void RequestFolderRename(AssetBrowserFolder folder);
    void RequestFolderDeletion(AssetBrowserFolder folder);
    bool DrawFolderRenameField(AssetBrowserFolder folder, float width = -1);
    bool IsRenamingFolder(AssetBrowserFolder folder);
    System.Numerics.Vector2 MeasureTreeText(string text);
    void DrawTreeText(string text, System.Numerics.Vector2 origin, System.Numerics.Vector2 minimum, System.Numerics.Vector2 maximum);
    bool DrawTreeSelectable(string label, bool selected);
    void DrawFolderDragSource(AssetBrowserFolder folder);
    void ApplyPendingRelocation();
    bool CanCopyFolder(AssetBrowserFolder folder);
    void CopyFolder(AssetBrowserFolder folder);
    bool CanCutFolder(AssetBrowserFolder folder);
    void CutFolder(AssetBrowserFolder folder);
    bool CanPasteFolder(AssetBrowserFolder folder);
    void PasteFolder(AssetBrowserFolder folder);
}
