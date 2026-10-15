using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetEntry[] referenceContextAssets = [];
    private ProjectWorkspace? referenceContextWorkspace;

    private bool CanCopyReferenceAssets(IReadOnlyList<AssetEntry> assets) => workspace != null && authoring != null && assets.Count > 0
        && assets.All(asset => workspace.AssetItems.Any(item => item.Id == asset.Id) && CanCopyAssetItem(workspace.FindAsset(asset.Id)));
    private bool CanCutReferenceAssets(IReadOnlyList<AssetEntry> assets) => CanCopyReferenceAssets(assets) && sessionHistory != null
        && projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating
        && !closeRequested && !closePromptOpen && closingDocument == null
        && assets.All(asset => workspace!.FindAsset(asset.Id).Asset is SourceCodeAsset ? CanCutSourceAsset(asset) : CanDeleteAsset(asset) || CanCutVisualScript(asset));

    private void CutReferenceAssets(AssetEntry[] assets)
    {
        if (!CanCutReferenceAssets(assets)) return;
        var owner = workspace!;
        var selected = assetBrowser.CaptureSelection(); var primary = assetBrowser.Primary;
        var folders = assetBrowser.CaptureContentFolderSelection(); var folderPrimary = assetBrowser.PrimaryContentFolder;
        var scope = assetBrowser.CaptureNavigationScope();
        try { CutAssetEntries(assets); }
        finally
        {
            if (ReferenceEquals(workspace, owner))
            {
                assetBrowser.RestoreSelection(selected, primary);
                assetBrowser.RestoreNavigationScope(scope);
                assetBrowser.RestoreContentFolderSelection(folders, folderPrimary);
                ActivateAssetPropertyContext();
            }
        }
    }
    private Action? DrawReferenceAssetActions(AssetEntry[] assets)
    {
        if (workspace == null) return null;
        if (assets.Length == 0) return DrawEmptyReferenceAssetActions();
        var owner = workspace; var single = assets.Length == 1; var entry = assets[0]; var item = owner.FindAsset(entry.Id);
        Action? action = null;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Edit asset...", "Menu"), "Ctrl+Enter", false, single && CanEditAssetContent(entry))) action = () => OpenBrowserAsset(entry);
        action = DrawBuildRootAssetActions(entry, SessionAuthoringHistory, assets.Select(asset => asset.Id).ToArray()) ?? action;
        if (DrawNativeMenuItem("Create derived asset", "", false, single && CanCreateDerivedAsset(entry))) action = () => CreateDerivedAsset(entry);
        if (DrawNativeMenuItem("Create replacing asset", "", false, single && CanCreateReplacingAsset(entry))) action = () => CreateReplacingAsset(entry);
        if (DrawNativeMenuItem("Clear archetype", "", false, single && CanClearAssetLink(entry, true))) action = () => ClearAssetLink(entry, true);
        if (DrawNativeMenuItem("Clear replaces", "", false, single && CanClearAssetLink(entry, false))) action = () => ClearAssetLink(entry, false);
        Gui.Separator();
        var canCopy = CanCopyReferenceAssets(assets);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, CanCutReferenceAssets(assets))) action = () => CutReferenceAssets(assets);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, canCopy)) action = () => CopyAssetEntries(assets, false);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy with dependencies", "Menu"), "Ctrl+Shift+C", false, canCopy)) action = () => CopyAssetEntries(assets, true);
        DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, false); DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, false);
        Gui.Separator();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy asset URL", "Menu"), "", false, single)) action = () => Gui.SetClipboardText(item.Location.ToString());
        DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, false);
        Gui.Separator();
        action = DrawReferenceLocationActions() ?? action;
        Gui.Separator();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Open asset file", "Menu"), "", false, single)) action = () => RequestOpenExternalAsset(item);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Open with text editor", "Menu"), "", false, single)) action = () => RequestOpenExternalAsset(item, true);
        var source = single ? AssetMainSource(item) : null;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Open source file", "Menu"), "", false, single)) action = () => OpenAssetSourceExternally(item);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Show in Explorer", "Menu"))) action = () => ExploreAssetEntry(assets[^1]);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Show source file in Explorer", "Menu"), "", false, single && source != null)) action = () => ExploreAssetSource(item);
        return action == null ? null : () =>
        {
            if (!ReferenceEquals(workspace, owner) || assets.Any(asset => !owner.AssetItems.Any(item => item.Id == asset.Id && SamePath(item.FullPath.ToOSPath(), asset.Path)))) return;
            action();
        };
    }
    private Action? DrawReferenceLocationActions()
    {
        Action? action = null;
        // These original shared-menu commands target the main Asset view, not DisplayedReferences.
        var folderLocation = assetBrowser.SelectedFolder;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Create folder", "Menu"), "", false, folderLocation != null && CanCreateAssetFolder(folderLocation)))
            action = () => RequestAssetFolderCreation(folderLocation!);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Add asset...", "Menu"), "Ctrl+I", false, CanAddAsset)) action = RequestAssetCreation;
        var seed = assetBrowser.SelectedEntries.ToArray();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Update selected assets from their source", "Menu"), "", false, CanReimportAssets(seed))) action = () => RequestAssetReimports(seed);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Update all assets with modified source", "Menu"), "", false, CanReimportAllModifiedAssets)) action = RequestAllModifiedAssetReimports;
        return action;
    }
    private Action? DrawEmptyReferenceAssetActions()
    {
        DrawNativeMenuItem(EditorLocalization.Widget("Edit asset...", "Menu"), "Ctrl+Enter", false, false);
        Gui.Separator();
        foreach (var (label, shortcut) in new[]
        {
            ("Cut", "Ctrl+X"), ("Copy", "Ctrl+C"), ("Copy with dependencies", "Ctrl+Shift+C"),
            ("Paste", "Ctrl+V"), ("Delete", "Delete"),
        }) DrawNativeMenuItem(label, shortcut, false, false);
        Gui.Separator();
        DrawNativeMenuItem(EditorLocalization.Widget("Copy asset URL", "Menu"), "", false, false); DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, false);
        Gui.Separator();
        var action = DrawReferenceLocationActions();
        Gui.Separator();
        DrawNativeMenuItem(EditorLocalization.Widget("Open asset file", "Menu"), "", false, false); DrawNativeMenuItem(EditorLocalization.Widget("Open with text editor", "Menu"), "", false, false);
        DrawNativeMenuItem(EditorLocalization.Widget("Open source file", "Menu"), "", false, false);
        var explore = CaptureAssetLocationExplore();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Show in Explorer", "Menu"), "", false, explore != null)) action = explore;
        DrawNativeMenuItem(EditorLocalization.Widget("Show source file in Explorer", "Menu"), "", false, false);
        return action;
    }

}
