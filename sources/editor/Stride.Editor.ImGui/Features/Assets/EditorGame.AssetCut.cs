using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanCutSelectedAssets => assetBrowser.SelectedContentFolders.Count > 0 ? CanCutAssetContent : CanCopySelectedAssets && sessionHistory != null && projectLoading == null
        && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating
        && !closeRequested && !closePromptOpen && closingDocument == null
        && assetBrowser.SelectedEntries.All(entry => workspace!.FindAsset(entry.Id).Asset is SourceCodeAsset ? CanCutSourceAsset(entry) : CanDeleteAsset(entry) || CanCutVisualScript(entry));

    private void CutSelectedAssets()
    {
        if (!CanCutSelectedAssets) return;
        if (assetBrowser.SelectedContentFolders.Count > 0) CutAssetContent();
        else CutAssetEntries(assetBrowser.SelectedEntries.ToArray());
    }
    private void CutAssetEntries(AssetEntry[] selected, bool writeClipboard = true)
    {
        if (selected.Length == 0) return;
        if (selected.Any(entry => !(workspace!.FindAsset(entry.Id).Asset is SourceCodeAsset ? CanCutSourceAsset(entry) : CanDeleteAsset(entry) || CanCutVisualScript(entry))))
            throw new InvalidOperationException("This asset selection can't be cut.");
        if (selected.All(CanCutSourceAsset)) { CutSourceAssets(selected, writeClipboard); return; }
        documents.CommitAll();
        var sources = selected.Where(CanCutSourceAsset).ToArray();
        var targets = selected.Except(sources).ToArray();
        foreach (var entry in targets.Where(CanCutVisualScript)) GetAssetTagDocument(entry, false);
        var retained = documents.AuthoredDocuments.OfType<IAssetReferenceDocument>()
            .ToDictionary(document => document.AuthoredAsset.Id);
        var plan = assetDeletionHistory.Prepare(workspace!, targets, retained,
            asset => (IAssetReferenceDocument)GetAssetTagDocument(asset, false), SetDeletedAssetDocuments, ForgetDeletedAssetDocuments,
            () => documents.AuthoredDocuments.OfType<IAssetReferenceDocument>()
                .ToDictionary(document => document.AuthoredAsset.Id, document => document.AuthoredAsset), preserveReferences: true);
        if (writeClipboard)
        {
            var text = SerializeAssetClipboard(selected.Select(entry => workspace!.FindAsset(entry.Id)).ToArray());
            if (string.IsNullOrEmpty(text)) return;
            Gui.SetClipboardText(text);
            if (Gui.GetClipboardTextS() != text) throw new InvalidOperationException("The assets could not be written to the clipboard.");
        }
        void Apply()
        {
            if (sources.Length != 0) CutSourceAssets(sources, false);
            CutVisualScriptOutputs(targets.Where(CanCutVisualScript));
            assetDeletionHistory.Delete(plan, false);
        }
        if (sessionHistory!.IsCapturingOperations) Apply();
        else { using var capture = sessionHistory.CaptureOperationsAtomic(Apply); capture.Publish(sessionHistory, "Cut selection"); }
        assetDeletionCommandContext = true;
        assetRelocationCommandContext = assetTagBatchCommandContext = packageFolderCommandContext = false;
        RefreshDeletedAssets(); ActivateAssetPropertyContext();
    }
}
