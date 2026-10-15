namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private enum HistoryTarget { None, Document, Relocation, Deletion, Tags, Folders, Blocked }
    private HistoryTarget CurrentHistoryTarget(bool redo)
    {
        if (IsPackageDependencyBusy || IsPackagePlatformUpdating) return HistoryTarget.Blocked;
        if (sessionHistory != null) return (redo ? sessionHistory.CanRedo : sessionHistory.CanUndo) ? HistoryTarget.Document : HistoryTarget.None;
        var active = CommandDocument;
        var deletion = assetDeletionHistory.GetOwnership(active, redo);
        var relocation = assetRelocationHistory.GetOwnership(active, redo);
        var tags = assetTagBatchHistory.GetOwnership(active, redo);
        if (deletion == GroupedHistoryOwnership.Ready) return HistoryTarget.Deletion;
        if (relocation == GroupedHistoryOwnership.Ready) return HistoryTarget.Relocation;
        if (tags == GroupedHistoryOwnership.Ready) return HistoryTarget.Tags;
        if (deletion == GroupedHistoryOwnership.Blocked || relocation == GroupedHistoryOwnership.Blocked || tags == GroupedHistoryOwnership.Blocked) return HistoryTarget.Blocked;
        if (assetDeletionCommandContext && (redo ? assetDeletionHistory.CanRedo : assetDeletionHistory.CanUndo)) return HistoryTarget.Deletion;
        if (assetRelocationCommandContext && (redo ? assetRelocationHistory.CanRedo : assetRelocationHistory.CanUndo)) return HistoryTarget.Relocation;
        if (assetTagBatchCommandContext && (redo ? assetTagBatchHistory.CanRedo : assetTagBatchHistory.CanUndo)) return HistoryTarget.Tags;
        if (packageFolderCommandContext) return (redo ? packageFolderHistory.CanRedo : packageFolderHistory.CanUndo) ? HistoryTarget.Folders : HistoryTarget.None;
        return (redo ? active?.CanRedo : active?.CanUndo) == true ? HistoryTarget.Document : HistoryTarget.None;
    }
    private bool CanUndoCurrent => CurrentHistoryTarget(false) is not (HistoryTarget.None or HistoryTarget.Blocked);
    private bool CanRedoCurrent => CurrentHistoryTarget(true) is not (HistoryTarget.None or HistoryTarget.Blocked);
    private void UndoCurrentCommand()
    {
        if (IsPackageDependencyBusy || IsPackagePlatformUpdating) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true);
        documents.CommitAll();
        if (sessionHistory != null) { if (TryPrepareEmptyWorkspaceHistoryReplay(false)) sessionHistory.Undo(); return; }
        switch (CurrentHistoryTarget(false))
        {
            case HistoryTarget.Relocation: assetRelocationHistory.Undo(); RefreshRelocatedAssets(); break;
            case HistoryTarget.Deletion: assetDeletionHistory.Undo(); RefreshDeletedAssets(); break;
            case HistoryTarget.Tags: assetTagBatchHistory.Undo(); SyncAuthoredAssetTags(); break;
            case HistoryTarget.Folders: packageFolderHistory.Undo(); break;
            case HistoryTarget.Document: CommandDocument?.Undo(); break;
        }
    }
    private void RedoCurrentCommand()
    {
        if (IsPackageDependencyBusy || IsPackagePlatformUpdating) return;
        ValidatePendingPropertyStrings();
        documents.CommitAll();
        if (sessionHistory != null) { if (TryPrepareEmptyWorkspaceHistoryReplay(true)) sessionHistory.Redo(); return; }
        switch (CurrentHistoryTarget(true))
        {
            case HistoryTarget.Relocation: assetRelocationHistory.Redo(); RefreshRelocatedAssets(); break;
            case HistoryTarget.Deletion: assetDeletionHistory.Redo(); RefreshDeletedAssets(); break;
            case HistoryTarget.Tags: assetTagBatchHistory.Redo(); SyncAuthoredAssetTags(); break;
            case HistoryTarget.Folders: packageFolderHistory.Redo(); break;
            case HistoryTarget.Document: CommandDocument?.Redo(); break;
        }
    }
    private void ReloadCurrentDocument()
    {
        switch (CommandDocument)
        {
            case PrefabDocument prefab: RequestReplace(() => OpenPrefab(prefab.Path, true)); break;
            case SceneDocument scene: RequestReplace(() => OpenScene(scene.Path, true)); break;
            case ProceduralAssetDocument asset: OpenAssetEditor(asset.Path, true); break;
            case ScriptSourceDocument script: RequestReplace(() => OpenScriptDocument(script.Path, true), true); break;
            case AssetMetadataDocument metadata:
                RequestReplace(() =>
                {
                    var entry = documents.Find(metadata) ?? throw new InvalidOperationException("The document is no longer open.");
                    ReplaceAuthoredDocument(entry, () => metadata is VisualScriptDocument
                        ? new VisualScriptDocument(metadata.Path, sessionHistory!, IsReadOnlyAssetPath(metadata.Path))
                        : new AssetMetadataDocument(metadata.Path, sessionHistory, IsReadOnlyAssetPath(metadata.Path)), "Reload " + entry.Name);
                    InvalidateCompiledTexture(metadata.Path);
                }, true);
                break;
        }
    }
}
