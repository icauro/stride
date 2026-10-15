using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanStartAssetEdit => workspace != null && authoring != null && sessionHistory != null && projectLoading == null
        && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating
        && !RecoveryTransitionBusy && !closeRequested && !closePromptOpen && closingDocument == null && !editorExtensionsStopping;
    private bool CanEditAssetContent(AssetEntry entry)
    {
        if (!CanStartAssetEdit || !workspace!.AssetItems.Any(item => item.Id == entry.Id)) return false;
        var item = workspace.FindAsset(entry.Id);
        if (!IsEditableAssetPackage(item.Package) || !SamePath(item.FullPath.ToOSPath(), entry.Path)) return false;
        var type = item.Asset.GetType();
        return sessionHistory!.Extensions.HasAssetEditor(type)
            || typeof(Stride.Assets.Entities.SceneAsset).IsAssignableFrom(type)
            || typeof(Stride.Assets.Entities.PrefabAsset).IsAssignableFrom(type)
            || typeof(Stride.Assets.Models.ProceduralModelAsset).IsAssignableFrom(type)
            || typeof(Stride.UI.Assets.UIPageAsset).IsAssignableFrom(type)
            || typeof(Stride.UI.Assets.UILibraryAsset).IsAssignableFrom(type)
            || typeof(Stride.Assets.Sprite.SpriteSheetAsset).IsAssignableFrom(type)
            || typeof(Stride.Assets.Rendering.GraphicsCompositorAsset).IsAssignableFrom(type)
            || typeof(Stride.Assets.Scripts.ScriptSourceFileAsset).IsAssignableFrom(type)
            || typeof(Stride.Assets.Scripts.VisualScriptAsset).IsAssignableFrom(type);
    }
    private bool CanEditSelectedAssetContent => CanStartAssetEdit && (assetBrowser.SelectedCount == 0 && assetBrowser.SelectedContentFolders.Count == 1
        || assetBrowser.SelectedContentFolders.Count == 0 && assetBrowser.SelectedCount == 1 && assetBrowser.PrimaryEntry is { } asset && CanEditAssetContent(asset));
    private void EditSelectedAssetContent()
    {
        if (!CanEditSelectedAssetContent) return;
        if (assetBrowser.SelectedContentFolders.SingleOrDefault() is { } folder)
        {
            if (!ReferenceEquals(folder.Package.Session, workspace!.Session) || assetBrowser.FindFolder(folder.Id) == null) return;
            assetBrowser.SelectFolder(folder); ActivateAssetPropertyContext(); ClearSelectedAssetPreview();
        }
        else if (assetBrowser.PrimaryEntry is { } asset && CanEditAssetContent(asset)) OpenBrowserAsset(asset);
    }
    private void ProcessAssetBrowserEditShortcut()
    {
        if (!CanEditSelectedAssetContent || Gui.GetIO().AppFocusLost || !Gui.GetIO().KeyCtrl || Gui.GetIO().KeyShift || Gui.GetIO().KeyAlt || Gui.GetIO().WantTextInput
            || Gui.IsAnyItemActive() || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;
        if (Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false)) Execute(EditSelectedAssetContent);
    }
}
