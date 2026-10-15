using System.Diagnostics;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string? pendingExternalAssetPath;
    private bool showExternalAssetSave;
    private bool pendingExternalTextEditor;
    private Func<bool>? pendingExternalAssetIntent;

    private Action? DrawAssetActions(AssetEntry entry)
    {
        var item = workspace!.FindAsset(entry.Path);
        var single = assetBrowser.SelectedContentFolders.Count == 0 && (!assetBrowser.IsSelected(entry.Id) || assetBrowser.SelectedCount == 1);
        Action? action = null;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Edit asset...", "Menu"), "Ctrl+Enter", false, single && CanEditAssetContent(entry))) action = () => OpenBrowserAsset(entry);
        var folderLocation = assetBrowser.SelectedFolder;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Create folder", "Menu"), "", false, folderLocation != null && CanCreateAssetFolder(folderLocation)))
            action = () => RequestAssetFolderCreation(folderLocation!);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Add asset...", "Menu"), "Ctrl+I", false, CanAddAsset)) action = RequestAssetCreation;
        var sourceTargets = assetBrowser.IsSelected(entry.Id) ? assetBrowser.SelectedEntries.ToArray() : [entry];
        if (DrawNativeMenuItem(EditorLocalization.Widget("Update selected assets from their source", "Menu"), "", false, CanReimportAssets(sourceTargets))) action = () => RequestAssetReimports(sourceTargets);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Update all assets with modified source", "Menu"), "", false, CanReimportAllModifiedAssets)) action = RequestAllModifiedAssetReimports;
        if (DrawNativeMenuItem("Create derived asset", "", false, single && CanCreateDerivedAsset(entry))) action = () => CreateDerivedAsset(entry);
        if (DrawNativeMenuItem("Create replacing asset", "", false, single && CanCreateReplacingAsset(entry))) action = () => CreateReplacingAsset(entry);
        if (DrawNativeMenuItem("Clear archetype", "", false, single && CanClearAssetLink(entry, true))) action = () => ClearAssetLink(entry, true);
        if (DrawNativeMenuItem("Clear replaces", "", false, single && CanClearAssetLink(entry, false))) action = () => ClearAssetLink(entry, false);
        var canCopy = assetBrowser.IsSelected(entry.Id) ? CanCopySelectedAssets : CanCopyAssetItem(item);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, canCopy)) action = () => CopySelectedAssets(false);
        var canCut = assetBrowser.IsSelected(entry.Id) ? CanCutSelectedAssets : canCopy && (CanDeleteAsset(entry) || CanCutSourceAsset(entry))
            && sessionHistory != null && projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding;
        canCut &= !closeRequested && !closePromptOpen && closingDocument == null && !IsAssetImportUpdating;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, canCut)) action = CutSelectedAssets;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy with dependencies", "Menu"), "Ctrl+Shift+C", false, assetBrowser.IsSelected(entry.Id) ? CanCopySelectedAssetsWithDependencies : CanCopyAssetItem(item))) action = () => CopySelectedAssets(true);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, CanPasteAssets())) action = PasteAssets;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, single && CanRelocateAsset(entry))) action = () => RequestAssetRename(entry);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, CanDeleteAsset(entry) && (!assetBrowser.IsSelected(entry.Id) || assetBrowser.SelectedEntries.All(CanDeleteAsset))))
            action = RequestAssetDeletion;
        action = DrawBuildRootAssetActions(entry, SessionAuthoringHistory) ?? action;
        Gui.Separator();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy asset URL", "Menu"), "", false, single)) action = () => Gui.SetClipboardText(item.Location.ToString());
        Gui.Separator();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Open asset file", "Menu"), "", false, single)) action = () => RequestOpenExternalAsset(item);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Open with text editor", "Menu"), "", false, single)) action = () => RequestOpenExternalAsset(item, true);
        var sourcePath = AssetMainSource(item);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Open source file", "Menu"), "", false, single)) action = () => OpenAssetSourceExternally(item);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Show in Explorer", "Menu"))) action = () => ExploreSelectedAssetContent(new(entry, null));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Show source file in Explorer", "Menu"), "", false, single && sourcePath != null)) action = () => ExploreAssetSource(item);
        return action;
    }

    private string? AssetMainSource(AssetItem item)
    {
        // Documents can retain a newer authored graph than the package snapshot.
        var current = documents.Find(item.FullPath.ToOSPath())?.Document;
        var asset = current is IAssetTagDocument tagged ? tagged.AuthoredAsset : item.Asset;
        var source = asset.MainSource?.ToOSPath();
        if (string.IsNullOrWhiteSpace(source)) return null;
        return Path.GetFullPath(source, Path.GetDirectoryName(item.FullPath.ToOSPath())!);
    }

    private void RequestOpenExternalAsset(AssetItem item, bool textEditor = false)
    {
        var path = item.FullPath.ToOSPath();
        var intent = CaptureExternalAssetIntent(item, path);
        if (!intent()) return;
        var openDocument = documents.Find(path)?.Document;
        openDocument?.CommitEdits();
        if (openDocument != null ? openDocument.IsDirty : item.IsDirty)
        {
            pendingExternalAssetPath = path; pendingExternalTextEditor = textEditor; pendingExternalAssetIntent = intent; showExternalAssetSave = true;
            return;
        }
        OpenAssetFileExternally(path, textEditor, intent);
    }

    private void DrawAssetActionDialog()
    {
        DrawAssetExploreMessage();
        var confirmationTitle = WpfMessageDialogTitle("OpenAssetSaveConfirmation");
        if (showExternalAssetSave) { Gui.OpenPopup(confirmationTitle); showExternalAssetSave = false; }
        if (!Gui.BeginPopupModal(confirmationTitle, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (pendingExternalAssetPath == null || pendingExternalAssetIntent?.Invoke() != true)
        { pendingExternalAssetPath = null; pendingExternalAssetIntent = null; Gui.CloseCurrentPopup(); Gui.EndPopup(); return; }
        DrawMessageDialogBody("##OpenAssetSaveMessage", EditorLocalization.Text(pendingExternalTextEditor
            ? "This asset has unsaved changes. To open it, you need to save the session first. Do you want to save now?"
            : "This asset has unsaved changes. To open it, you need to save it first. Do you want to save the session now?", "Message"), EditorMessageDialogImage.Information);
        var cancelLabel = EditorLocalization.Widget("Cancel", "Button");
        var ownsDialogKeys = !Gui.GetIO().AppFocusLost && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        var cancel = ownsDialogKeys && Gui.IsKeyPressed(ImGuiKey.Escape, false);
        var accept = ownsDialogKeys && !cancel && Gui.GetCurrentContext().NavId != Gui.GetID(cancelLabel) && Gui.IsKeyPressed(ImGuiKey.Enter, false);
        if (accept) ImGuiP.SetKeyOwner(ImGuiKey.Enter, Gui.GetID("ExternalAssetSave"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
        if (cancel) ImGuiP.SetKeyOwner(ImGuiKey.Escape, Gui.GetID("ExternalAssetCancel"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
        using var pendingSave = ImGuiScopes.Disabled(IsPackageMetadataSaving);
        var saveClicked = DrawNativeButton(EditorLocalization.Widget("Save", "Button"));
        if (!cancel && (saveClicked || accept)) Execute(() =>
        {
            var path = pendingExternalAssetPath;
            var intent = pendingExternalAssetIntent;
            if (path == null || intent == null || !intent()) { pendingExternalAssetPath = null; pendingExternalAssetIntent = null; Gui.CloseCurrentPopup(); return; }
            var textEditor = pendingExternalTextEditor;
            SaveAllDocuments(() =>
            {
            if (!ReferenceEquals(pendingExternalAssetIntent, intent) || pendingExternalAssetPath != path)
                throw new InvalidOperationException("The external open intent changed while saving.");
            if (!intent()) { pendingExternalAssetPath = null; pendingExternalAssetIntent = null; return; }
            var item = workspace!.FindAsset(path);
            if (item.IsDirty && documents.Find(path) == null)
            {
                var log = new LoggerResult();
                workspace.Session.Save(log, new PackageSaveParameters
                {
                    AssetFilter = candidate => candidate.Id == item.Id,
                    PackageFilter = package => ReferenceEquals(package, item.Package) && !package.IsReadOnly,
                    PackageWrite = SaveOwnedPackageMetadata,
                });
                if (log.HasErrors) throw new InvalidOperationException(log.ToText());
            }
            var authored = documents.Find(path)?.Document;
            if (authored != null ? authored.IsDirty : item.IsDirty)
                throw new InvalidOperationException("This asset has unsaved changes.");
            OpenAssetFileExternally(path, textEditor, intent);
            pendingExternalAssetPath = null; pendingExternalAssetIntent = null;
            });
        });
        Gui.SameLine();
        if (DrawNativeButton(cancelLabel) || cancel) { pendingExternalAssetPath = null; pendingExternalAssetIntent = null; Gui.CloseCurrentPopup(); }
        Gui.EndPopup();
    }

    private void ClearAssetActions() { pendingExternalAssetPath = null; pendingExternalAssetIntent = null; showExternalAssetSave = false; pendingExternalTextEditor = false; ClearAssetExploreMessages(); }


    private void RevealExternalFile(string path, bool assetFile = false) => StartAssetExplorer(path, true, assetFile);
}
