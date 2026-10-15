using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Explicit access to the real workspace and feature services used by browser presentation.</summary>
internal interface IAssetBrowserPanelHost
{
    ProjectWorkspace? Workspace { get; }
    bool CanEditSelectedContent { get; }
    void EditSelectedContent();
    bool CanAddAssets { get; }
    void RequestAddition();
    bool CanImportAssets { get; }
    void RequestImportFiles(string[] files);
    bool CanUpdateModifiedSources { get; }
    int ModifiedSourceCount { get; }
    void UpdateModifiedSources();
    bool CanCopyAssets { get; }
    bool CanCopyAssetDependencies { get; }
    void CopyAssets(bool includeDependencies);
    bool CanCutAssets { get; }
    void CutAssets();
    bool CanPasteAssets { get; }
    void PasteAssets();
    int PendingEffectCount { get; }
    bool CanImportEffectLog { get; }
    void ImportEffectLog();
    EditorIcons Icons { get; }
    bool CaptureControls { get; }
    void RecordControl(string id);
    void BeginThumbnails();
    void SyncAuthoredTags();
    void OnAssetSelectionChanged();
    bool IsDirty(AssetEntry asset);
    void RequestDeletion();
    void RequestRename(AssetEntry asset);
    bool IsRenaming(AssetEntry asset);
    bool DrawRenameField(AssetEntry asset, float width = -1);
    void DrawThumbnail(AssetEntry asset, NVector2 origin, float size);
    void DrawTooltip(AssetEntry asset);
    void DrawFolderTooltip(AssetBrowserFolder folder);
    Action? DrawTileBadges(AssetEntry asset, NVector2 origin, float size);
    void DrawDragSource(AssetEntry asset);
    Action? DrawActions(AssetEntry asset);
    Action? DrawBackgroundActions();
    void OpenAsset(AssetEntry asset);
    void PreviewAsset(AssetEntry asset);
    void Execute(Action action);
    void ApplyPendingRelocation();
    void SaveViewPreferences(bool saveThumbnail = false);
    int SavedGridThumbnailSize { get; }
    void SaveCollectionPreferences();
    AssetBrowserSort SavedSort { get; }
    bool SavedIncludeDescendants { get; }
    AssetBrowserDisplayMode SavedDisplayMode { get; }
    void RefreshFolders();
    Action? DrawFolderActions(AssetBrowserFolder folder);
    void DrawFolderDropTarget(AssetBrowserFolder folder);
    void DrawFolderDragSource(AssetBrowserFolder folder);
    bool IsRenamingFolder(AssetBrowserFolder folder);
    bool DrawFolderRenameField(AssetBrowserFolder folder, float width = -1);
    void RequestFolderRename(AssetBrowserFolder folder);
    IReadOnlyList<AssetFilterPreference> SavedFilters { get; }
    void SaveFilters();
    NVector2 MeasureLabel(string text);
    void PaintLabel(string text, NVector2 origin, NVector2 minimum, NVector2 maximum, uint color);
    void DrawLabel(string text);
    bool DrawButton(string label);
    bool DrawSmallButton(string label);
    bool BeginMenu(string label, bool enabled = true);
    bool DrawMenuItem(string label, string? shortcut = null, bool selected = false, bool enabled = true);
}
