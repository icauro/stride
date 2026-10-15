using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action? DrawAssetBackgroundActions()
    {
        if (workspace == null) return null;
        var assets = assetBrowser.SelectedEntries.ToArray();
        var folders = assetBrowser.SelectedContentFolders;
        // An asset-only selection has the same real command menu as a row click.
        if (folders.Count == 0 && assets.FirstOrDefault() is { } selected)
            return DrawAssetActions(assetBrowser.PrimaryEntry ?? selected);
        Action? action = null;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Edit asset...", "Menu"), "Ctrl+Enter", false, CanEditSelectedAssetContent)) action = EditSelectedAssetContent;
        if (assets.FirstOrDefault() is { } rootEntry)
            action = DrawBuildRootAssetActions(rootEntry, SessionAuthoringHistory) ?? action;
        Gui.Separator();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, CanCutSelectedAssets)) action = CutSelectedAssets;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, CanCopySelectedAssets)) action = () => CopySelectedAssets(false);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy with dependencies", "Menu"), "Ctrl+Shift+C", false, CanCopySelectedAssetsWithDependencies)) action = () => CopySelectedAssets(true);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, CanPasteAssets())) action = PasteAssets;
        var canDelete = CanStartAssetEdit && assets.Length + folders.Count > 0 && assets.All(CanDeleteAsset)
            && SelectedAssetContentRoots().All(CanCutAssetFolder);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, canDelete)) action = RequestAssetDeletion;
        Gui.Separator();
        DrawNativeMenuItem(EditorLocalization.Widget("Copy asset URL", "Menu"), "", false, false);
        var rename = assets.Length == 0 && folders.Count == 1 ? folders[0] : null;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, rename != null && CanRelocateAssetFolder(rename)))
            action = () => RequestAssetFolderRename(rename!);
        Gui.Separator();
        var location = assetBrowser.SelectedFolder;
        if (location != null && CanCreateAssetFolder(location) && DrawNativeMenuItem(EditorLocalization.Widget("Create folder", "Menu")))
            action = () => RequestAssetFolderCreation(location);
        if (CanAddAsset && DrawNativeMenuItem(EditorLocalization.Widget("Add asset...", "Menu"), "Ctrl+I")) action = RequestAssetCreation;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Update selected assets from their source", "Menu"), "", false, CanReimportAssets(assets)))
            action = () => RequestAssetReimports(assets);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Update all assets with modified source", "Menu"), "", false, CanReimportAllModifiedAssets)) action = RequestAllModifiedAssetReimports;
        Gui.Separator();
        DrawNativeMenuItem(EditorLocalization.Widget("Open with text editor", "Menu"), "", false, false);
        DrawNativeMenuItem(EditorLocalization.Widget("Open asset file", "Menu"), "", false, false);
        DrawNativeMenuItem(EditorLocalization.Widget("Open source file", "Menu"), "", false, false);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Show in Explorer", "Menu"), "", false, assets.Length + folders.Count > 0 || assetBrowser.SelectedLocationCount == 1))
            action = () => ExploreSelectedAssetContent();
        DrawNativeMenuItem(EditorLocalization.Widget("Show source file in Explorer", "Menu"), "", false, false);
        return action;
    }
}
