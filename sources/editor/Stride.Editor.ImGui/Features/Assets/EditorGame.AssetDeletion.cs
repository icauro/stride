using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetDeletionHistory assetDeletionHistory = new();
    private bool assetDeletionCommandContext;
    private AssetEntry[] deletingAssets = [];
    private AssetDeletionHistory.Plan? pendingAssetDeletion;
    private HashSet<Guid> deletionExistingDocuments = [];
    private bool showAssetDeletion, showAssetReferenceDeletion, alwaysDeleteAssets;
    private string? assetDeletionError;
    private bool preserveIndependentDeletionSelection;
    private Func<bool>? assetDeletionContextCurrent;
    private Action<bool>? assetDeletionContextCompleted;
    private Func<bool, bool>? assetDeletionCommitCoordinator;
    private void CompleteAssetDeletionContext(bool succeeded)
    {
        var completed = assetDeletionContextCompleted;
        assetDeletionContextCurrent = null; assetDeletionContextCompleted = null; assetDeletionCommitCoordinator = null;
        if (completed != null) Execute(() => completed(succeeded));
    }
    private bool CanDeleteAsset(AssetEntry asset)
    {
        if (!CanStartAssetEdit || workspace!.AssetItems.FirstOrDefault(candidate => candidate.Id == asset.Id && !candidate.IsDeleted) is not { } item
            || !SamePath(item.FullPath.ToOSPath(), asset.Path)) return false;
        return IsCurrentEditableAssetPackage(item.Package) && (item.Asset is not IProjectAsset || item.Asset is SourceCodeAsset && (item.Asset is not IProjectAsset || item.Package.Container is SolutionProject) || item.Asset is Stride.Assets.Scripts.VisualScriptAsset && item.Package.Container is SolutionProject) && item.AlternativePath == null;
    }
    private void RequestAssetDeletion()
        => RequestAssetDeletion(assetBrowser);
    private void RequestAssetDeletion(AssetBrowserState selection, Func<bool>? isCurrent = null, Action<bool>? completed = null, Func<bool, bool>? coordinateCommit = null)
    {
        if (!CanStartAssetEdit || selection.SelectedCount == 0 && selection.SelectedContentFolders.Count == 0 || isCurrent?.Invoke() == false)
        { completed?.Invoke(false); return; }
        CompleteAssetDeletionContext(false);
        ClearAssetFolderDeletionPreparation();
        deletingAssets = GatherAssetContentDeletionTargets(selection);
        if (!deletingAssets.All(CanDeleteAsset)) { completed?.Invoke(false); return; }
        ValidatePendingPropertyStrings();
        assetDeletionContextCurrent = isCurrent; assetDeletionContextCompleted = completed; assetDeletionCommitCoordinator = coordinateCommit;
        preserveIndependentDeletionSelection = !ReferenceEquals(selection, assetBrowser);
        pendingAssetDeletion = null; assetDeletionError = null; alwaysDeleteAssets = false;
        deletionExistingDocuments = documents.Entries.Select(entry => entry.Id).ToHashSet();
        if (preferences?.Current.AskBeforeDeletingAssets != false) showAssetDeletion = true;
        else TryPrepareAssetDeletion(false);
    }
    private void DrawAssetDeletionDialog()
    {
        var confirmationTitle = WpfMessageDialogTitle("DeleteAssetsConfirmation");
        if (showAssetDeletion) { Gui.OpenPopup(confirmationTitle); showAssetDeletion = false; }
        if (Gui.BeginPopupModal(confirmationTitle, ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawSelectableText("##DeleteAssetsMessage", AssetContentDeletionMessage());
            if (assetDeletionError != null) DrawWrappedText(assetDeletionError);
            Gui.Checkbox(EditorLocalization.Widget("Always delete without asking", "Settings"), ref alwaysDeleteAssets);
            if (Gui.Button(EditorLocalization.Widget("Delete", "Button")))
            {
                if (alwaysDeleteAssets && preferences != null)
                {
                    var previous = preferences.Current.AskBeforeDeletingAssets;
                    preferences.Current.AskBeforeDeletingAssets = false;
                    if (!preferences.Save()) { preferences.Current.AskBeforeDeletingAssets = previous; assetDeletionError = preferences.Error; }
                    else TryPrepareAssetDeletion();
                }
                else TryPrepareAssetDeletion();
            }
            Gui.SameLine();
            if (Gui.Button(EditorLocalization.Widget("Cancel", "Button"))) { CancelAssetDeletion(); Gui.CloseCurrentPopup(); }
            Gui.EndPopup();
        }
        if (showAssetReferenceDeletion) { Gui.OpenPopup("Fix references###FixAssetDeletionRefs"); showAssetReferenceDeletion = false; }
        var referenceTitle = string.Format(EditorLocalization.Text("Fix references ({0})"),
            deletionReferenceIndex + "/" + (pendingAssetDeletion?.ReferencedTargets.Count ?? 0)) + "###FixAssetDeletionRefs";
        if (!Gui.BeginPopupModal(referenceTitle, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (pendingAssetDeletion is not { } plan) { Gui.CloseCurrentPopup(); Gui.EndPopup(); return; }
        DrawAssetDeletionReferences(plan);
        Gui.EndPopup();
    }
    private void TryPrepareAssetDeletion(bool popupContext = true)
    {
        try
        {
            if (!CanStartAssetEdit || assetDeletionContextCurrent?.Invoke() == false || !deletingAssets.All(CanDeleteAsset))
                throw new InvalidOperationException("The selected assets are no longer available for deletion.");
            ValidatePendingPropertyStrings();
            var retained = documents.AuthoredDocuments.OfType<IAssetReferenceDocument>().ToDictionary(document => document.AuthoredAsset.Id);
            PrepareSourceAssetDeletionOwners();
            var serializedTargets = deletingAssets.Where(entry => !typeof(SourceCodeAsset).IsAssignableFrom(entry.AssetType)).ToArray();
            pendingAssetDeletion = deletingAssets.Length == 0 ? null : assetDeletionHistory.Prepare(workspace!, serializedTargets, retained,
                asset => (IAssetReferenceDocument)GetAssetTagDocument(asset, false), SetDeletedAssetDocuments, ForgetDeletedAssetDocuments,
                () => documents.AuthoredDocuments.OfType<IAssetReferenceDocument>()
                    .ToDictionary(document => document.AuthoredAsset.Id, document => document.AuthoredAsset), referenceTargets: deletingAssets);
            assetDeletionError = null;
            ResetDeletionReferenceChoices();
            if (pendingAssetDeletion?.HasReferences == true)
            {
                if (popupContext) Gui.CloseCurrentPopup();
                showAssetReferenceDeletion = true;
            }
            else TryCommitAssetDeletion(false, popupContext);
        }
        catch (Exception error)
        {
            assetDeletionError = error.Message;
            if (!popupContext) showAssetDeletion = true;
        }
    }
    private void TryCommitAssetDeletion(bool clearReferences, bool popupContext = true)
    {
        var committed = false;
        try
        {
            if (assetDeletionContextCurrent?.Invoke() == false) throw new InvalidOperationException("The asset picker target changed before deletion.");
            if (assetDeletionCommitCoordinator?.Invoke(clearReferences) == true)
            { if (popupContext) Gui.CloseCurrentPopup(); return; }
            CommitAssetContentDeletion(clearReferences);
            committed = true;
            FinishCommittedAssetDeletion(popupContext);
        }
        catch (Exception error)
        {
            assetDeletionError = error.Message; pendingAssetDeletion = null;
            ResetDeletionReferenceChoices(); ClearAssetFolderDeletionPreparation();
            if (popupContext) Gui.CloseCurrentPopup();
            showAssetDeletion = true;
        }
        if (committed) CompleteAssetDeletionContext(true);
    }
    private void FinishCommittedAssetDeletion(bool popupContext)
    {
        assetDeletionCommandContext = true;
        assetRelocationCommandContext = assetTagBatchCommandContext = packageFolderCommandContext = false;
        RefreshDeletedAssets();
        pendingAssetDeletion = null; deletingAssets = []; assetDeletionError = null; deletionExistingDocuments.Clear();
        ResetDeletionReferenceChoices(); ClearAssetFolderDeletionPreparation();
        if (popupContext) Gui.CloseCurrentPopup();
    }
    private void CancelAssetDeletion()
    {
        pendingAssetDeletion = null; deletingAssets = []; assetDeletionError = null;
        foreach (var entry in documents.Entries.Where(entry => !deletionExistingDocuments.Contains(entry.Id)
            && !entry.Document.IsDirty && !ReferenceEquals(documents.Active, entry)).ToArray()) CloseDocument(entry);
        deletionExistingDocuments.Clear();
        ResetDeletionReferenceChoices(); ClearAssetFolderDeletionPreparation();
        CompleteAssetDeletionContext(false);
    }
    private void RefreshDeletedAssets()
    {
        if (workspace == null) return;
        var contentFolders = assetBrowser.CaptureContentFolderSelection(); var contentPrimary = assetBrowser.PrimaryContentFolder; var scope = assetBrowser.CaptureNavigationScope();
        var selected = preserveIndependentDeletionSelection || contentFolders.Length > 0 ? assetBrowser.CaptureSelection() : assetDeletionHistory.DesiredSelection;
        assetBrowser.RefreshEntries(workspace);
        assetBrowser.RestoreSelection(selected, preserveIndependentDeletionSelection ? assetBrowser.Primary : selected.Length > 0 ? selected[0] : null);
        assetBrowser.RestoreNavigationScope(scope); assetBrowser.RestoreContentFolderSelection(contentFolders, contentPrimary);
        SyncAuthoredAssetTags(); assetReferences.Clear(); InvalidateAssetThumbnails(null);
    }
    private void ClearAssetDeletionState()
    {
        assetDeletionHistory.Clear(); assetDeletionCommandContext = false;
        CompleteAssetDeletionContext(false);
        deletingAssets = []; pendingAssetDeletion = null; deletionExistingDocuments.Clear();
        showAssetDeletion = showAssetReferenceDeletion = false; assetDeletionError = null;
        preserveIndependentDeletionSelection = false;
        ResetDeletionReferenceChoices(); ClearAssetFolderDeletionPreparation();
    }
}
