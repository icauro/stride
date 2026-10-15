using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    /// <summary>Adapts existing authored commands and engine-backed services without moving them into UI code.</summary>
    private sealed class AssetBrowserPanelHost(EditorGame game) : IAssetBrowserPanelHost
    {
        public ProjectWorkspace? Workspace => game.workspace;
        public bool CanEditSelectedContent => game.CanEditSelectedAssetContent;
        public void EditSelectedContent() => game.Execute(game.EditSelectedAssetContent);
        public bool CanAddAssets => game.CanAddAsset;
        public void RequestAddition() => game.RequestAssetCreation();
        public bool CanImportAssets => game.CanAddAsset && game.assetBrowser.SelectedFolder is { };
        public void RequestImportFiles(string[] files)
        {
            var folder = game.assetBrowser.SelectedFolder!;
            game.RequestDroppedAssetSources(files, folder);
        }
        public bool CanUpdateModifiedSources => game.CanReimportAllModifiedAssets;
        public int ModifiedSourceCount => game.ModifiedSourceAssetCount;
        public void UpdateModifiedSources() => game.Execute(game.RequestAllModifiedAssetReimports);
        public bool CanCopyAssets => game.CanCopySelectedAssets;
        public bool CanCopyAssetDependencies => game.CanCopySelectedAssetsWithDependencies;
        public void CopyAssets(bool includeDependencies) => game.CopySelectedAssets(includeDependencies);
        public bool CanCutAssets => game.CanCutSelectedAssets;
        public void CutAssets() => game.CutSelectedAssets();
        public bool CanPasteAssets => game.CanPasteAssets();
        public void PasteAssets() => game.PasteAssets();
        public int PendingEffectCount => game.PendingEffectCount;
        public bool CanImportEffectLog => game.CanImportEffectLog;
        public void ImportEffectLog() => game.Execute(game.ImportEffectLog);
        public void DrawTooltip(AssetEntry asset) => game.DrawAssetTooltip(asset);
        public void DrawFolderTooltip(AssetBrowserFolder folder) => game.DrawAssetFolderTooltip(folder);
        public EditorIcons Icons => game.icons!;
        public bool CaptureControls => game.Controls.Enabled;
        public void RecordControl(string id) => game.Controls.Record(id);
        public void BeginThumbnails() { game.Execute(game.UpdateAssetBuildInclusion); game.BeginAssetThumbnails(); }
        public void SyncAuthoredTags() => game.SyncAuthoredAssetTags();
        public void OnAssetSelectionChanged()
        {
            game.ActivateAssetPropertyContext();
            if (game.assetBrowser.SelectedCount != 1 || game.assetBrowser.SelectedContentFolders.Count != 0)
                game.ClearSelectedAssetPreview();
        }
        public bool IsDirty(AssetEntry asset) => game.documents.Find(asset.Path)?.Document.IsDirty ?? game.workspace?.FindAsset(asset.Id).IsDirty ?? false;
        public void RequestDeletion() => game.Execute(game.RequestAssetDeletion);
        public void RequestRename(AssetEntry asset) => game.RequestAssetRename(asset);
        public bool IsRenaming(AssetEntry asset) => game.renamingAsset == asset.Id;
        public bool DrawRenameField(AssetEntry asset, float width = -1) => game.DrawAssetRenameField(asset, width);
        public void DrawThumbnail(AssetEntry asset, NVector2 origin, float size) => game.DrawAssetThumbnail(asset, origin, size);
        public Action? DrawTileBadges(AssetEntry asset, NVector2 origin, float size) => game.DrawAssetTileBadges(asset, origin, size);
        public void DrawDragSource(AssetEntry asset) => game.DrawAssetLocationDragSource(asset);
        public Action? DrawActions(AssetEntry asset) => game.DrawAssetActions(asset);
        public Action? DrawBackgroundActions() => game.DrawAssetBackgroundActions();
        public void OpenAsset(AssetEntry asset) => game.OpenBrowserAsset(asset);
        public void PreviewAsset(AssetEntry asset) => game.PreviewBrowserAsset(asset);
        public void Execute(Action action) => game.Execute(action);
        public void ApplyPendingRelocation() => game.ApplyPendingAssetRelocation();
        public IReadOnlyList<AssetFilterPreference> SavedFilters => game.preferences?.Current.AssetFilters ?? [];
        public void SaveFilters() => game.SaveAssetBrowserFilters();
        public AssetBrowserSort SavedSort => game.preferences?.Current.AssetSort ?? AssetBrowserSort.TypeThenName;
        public bool SavedIncludeDescendants => game.preferences?.Current.AssetIncludeDescendants ?? false;
        public AssetBrowserDisplayMode SavedDisplayMode => game.preferences?.Current.AssetDisplayMode
            ?? (SavedIncludeDescendants ? AssetBrowserDisplayMode.RecursiveAssets : AssetBrowserDisplayMode.AssetsAndFolders);
        public void RefreshFolders() => game.SolutionExplorerController.RefreshFolders();
        public void DrawFolderDropTarget(AssetBrowserFolder folder) => game.DrawAssetFolderDropTarget(folder);
        public void DrawFolderDragSource(AssetBrowserFolder folder) => game.DrawAssetFolderDragSource(folder);
        public bool IsRenamingFolder(AssetBrowserFolder folder) => game.assetFolderRename.Id == folder.Id;
        public bool DrawFolderRenameField(AssetBrowserFolder folder, float width = -1) => game.DrawAssetFolderRenameField(folder, width);
        public void RequestFolderRename(AssetBrowserFolder folder) => game.RequestAssetFolderRename(folder);
        public Action? DrawFolderActions(AssetBrowserFolder folder)
        {
            Action? action = null;
            var selected = game.assetBrowser.SelectedContentFolders.Any(candidate => candidate.Id == folder.Id);
            if (game.DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, selected ? game.CanCopyAssetContent : game.CanCopyAssetFolder(folder))) action = game.CopyAssetContent;
            if (game.DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, selected ? game.CanCutAssetContent : game.CanCutAssetFolder(folder))) action = game.CutAssetContent;
            if (game.DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, game.CanPasteAssets(folder)))
                action = () => { game.assetBrowser.SelectFolder(folder); game.PasteAssets(); };
            if (game.DrawNativeMenuItem(EditorLocalization.Widget("New folder", "Menu"), "", false, game.CanCreateAssetFolder(folder)))
                action = () => game.RequestAssetFolderCreation(folder);
            if (game.DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, game.CanRelocateAssetFolder(folder))) action = () => game.RequestAssetFolderRename(folder);
            if (game.DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, game.CanCutAssetFolder(folder))) action = () => game.RequestAssetFolderDeletion(folder);
            if (game.DrawNativeMenuItem(EditorLocalization.Widget("Show in Explorer", "Menu"))) action = () => game.ExploreSelectedAssetContent(new(null, folder));
            return action;
        }
        public void SaveCollectionPreferences() => game.SaveAssetCollectionPreferences();
        public int SavedGridThumbnailSize => game.preferences?.Current.AssetGridThumbnailSize ?? 16;
        public void SaveViewPreferences(bool saveThumbnail = false) => game.SaveAssetViewPreferences(saveThumbnail);
        public NVector2 MeasureLabel(string text) => game.MeasureNativeLabel(text);
        public void PaintLabel(string text, NVector2 origin, NVector2 minimum, NVector2 maximum, uint color)
            => game.DrawNativeTreeText(text, origin, minimum, maximum, color);
        public void DrawLabel(string text) => game.DrawNativeLabel(text);
        public bool DrawButton(string label) => game.DrawNativeButton(label);
        public bool DrawSmallButton(string label) => game.DrawNativeSmallButton(label);
        public bool BeginMenu(string label, bool enabled = true) => game.BeginNativeMenu(label, enabled);
        public bool DrawMenuItem(string label, string? shortcut = null, bool selected = false, bool enabled = true)
            => game.DrawNativeMenuItem(label, shortcut, selected, enabled);
    }
}
