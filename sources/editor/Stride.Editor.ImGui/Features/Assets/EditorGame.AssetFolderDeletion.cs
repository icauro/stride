using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetBrowserFolder[] deletingAssetFolders = [];
    private int deletionExplicitAssetCount, deletionExplicitFolderCount;
    private readonly Dictionary<ScriptSourceDocument, long> deletingSourceVersions = [];
    private readonly Dictionary<string, AssetId[]> deletingFolderContents = new(StringComparer.OrdinalIgnoreCase);
    private void ClearAssetFolderDeletionPreparation()
    { deletingAssetFolders = []; deletingSourceVersions.Clear(); deletingFolderContents.Clear(); }
    private void RequestAssetFolderDeletion(AssetBrowserFolder folder)
    {
        if (!CanStartAssetEdit || !CanCutAssetFolder(folder)) return;
        if (!assetBrowser.SelectedContentFolders.Any(candidate => candidate.Id == folder.Id))
            assetBrowser.SelectContent(new AssetBrowserContent(null, folder), false, false);
        RequestAssetDeletion();
    }
    private AssetEntry[] GatherAssetContentDeletionTargets()
        => GatherAssetContentDeletionTargets(assetBrowser);
    private AssetEntry[] GatherAssetContentDeletionTargets(AssetBrowserState selection)
    {
        deletionExplicitAssetCount = selection.SelectedEntries.Count();
        var folders = selection.SelectedContentFolders.ToArray(); deletionExplicitFolderCount = folders.Length;
        deletingAssetFolders = folders.Where(folder => !folders.Any(parent => parent.Id != folder.Id
            && folder.Id.StartsWith(parent.Id + "/", StringComparison.OrdinalIgnoreCase)))
            .Select(folder => assetBrowser.FindFolder(folder.Id) ?? throw new InvalidOperationException("The selected folder is no longer current.")).ToArray();
        if (deletingAssetFolders.Any(folder => !CanCutAssetFolder(folder))) throw new InvalidOperationException("This folder can't be deleted.");
        var ids = selection.SelectedEntries.Select(entry => entry.Id).ToHashSet();
        foreach (var folder in deletingAssetFolders)
        {
            var contained = AssetFolderClipboardItems(folder).Select(item => item.Id).OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
            deletingFolderContents[folder.Id] = contained; ids.UnionWith(contained);
        }
        return ids.Select(id => workspace!.Assets.Single(entry => entry.Id == id)).ToArray();
    }
    private void PrepareSourceAssetDeletionOwners()
    {
        deletingSourceVersions.Clear();
        foreach (var entry in deletingAssets.Where(entry => typeof(SourceCodeAsset).IsAssignableFrom(entry.AssetType)))
        {
            if (!CanCutSourceAsset(entry)) throw new InvalidOperationException("The source can't be deleted.");
            var source = GetCanonicalDocument(entry.Path, () => CreateScriptDocument(entry.Path)) as ScriptSourceDocument
                ?? throw new InvalidOperationException("The source has another authored owner.");
            source.CommitEdits();
            if (!source.CanEditText) throw new InvalidOperationException("The source can't be deleted.");
            deletingSourceVersions[source] = source.Version;
        }
    }
    private void CommitAssetContentDeletion(bool clearReferences)
    {
        if (!CanStartAssetEdit || !deletingAssets.All(CanDeleteAsset))
            throw new InvalidOperationException("The selected assets are no longer available for deletion.");
        foreach (var (source, version) in deletingSourceVersions)
            if (source.Version != version || !source.CanEditText || !ReferenceEquals(authoring!.Find(source.Path)?.Document, source))
                throw new InvalidOperationException("The source changed. Select it again before deleting.");
        foreach (var folder in deletingAssetFolders)
        {
            if (!CanCutAssetFolder(folder) || assetBrowser.FindFolder(folder.Id) == null
                || !AssetFolderClipboardItems(folder).Select(item => item.Id).OrderBy(id => id.ToString(), StringComparer.Ordinal).SequenceEqual(deletingFolderContents[folder.Id]))
                throw new InvalidOperationException("The folder changed. Select it again before deleting.");
        }
        ValidatePendingPropertyStrings();
        var directories = deletingAssetFolders.Where(folder => folder.IsCode).ToDictionary(folder => folder.Id,
            folder => new CutSourceDirectoryOperation(this, folder, AssetFolderClipboardItems(folder)));
        var scope = assetBrowser.CaptureNavigationScope(); var selected = assetBrowser.CaptureSelection(); var primary = assetBrowser.Primary;
        var folderSelection = assetBrowser.CaptureContentFolderSelection(); var folderPrimary = assetBrowser.PrimaryContentFolder;
        var history = sessionHistory ?? throw new InvalidOperationException("Deletion requires the actual authored session.");
        var navigationPackage = deletingAssetFolders.FirstOrDefault()?.Package ?? workspace!.FindAsset(deletingAssets[0].Id).Package;
        var navigationParticipant = history.Registry.GetOrCreate(navigationPackage.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(workspace!, navigationPackage, history));
        void Apply()
        {
            history.PushOperation(new FolderNavigationBoundaryOperation(() => { RestoreFolderNavigation(scope, selected, primary, folderSelection, folderPrimary); assetDeletionHistory.SetDesiredSelection(selected); }, () => { }), navigationParticipant);
            CutVisualScriptOutputs(deletingAssets.Where(CanCutVisualScript));
            if (pendingAssetDeletion is { } plan && (plan.Deleted.Length != 0 || plan.HasReferences)) assetDeletionHistory.Delete(plan, clearReferences);
            var sources = deletingAssets.Where(entry => typeof(SourceCodeAsset).IsAssignableFrom(entry.AssetType)).ToArray();
            if (sources.Length != 0) CutSourceAssets(sources, false);
            foreach (var folder in deletingAssetFolders)
            {
                if (!folder.IsCode) { packageFolderHistory.RemoveFolder(folder.Package, folder.RelativePath); continue; }
                var project = folder.Package.Container as SolutionProject ?? throw new InvalidOperationException("The code folder requires an actual project.");
                var document = GetProjectBuildSettingsDocument(project, history); document.ValidateProjectFileBaseline();
                foreach (var include in document.GetProjectItemPaths("Folder").ToArray())
                {
                    var relative = include.Replace('\\', '/').TrimEnd('/');
                    if (relative.Equals(folder.RelativePath, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(folder.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                        document.ChangeProjectItem("Folder", include, false);
                }
                var operation = directories[folder.Id];
                if (!operation.HasEffect) continue;
                var participant = authoring!.Find(project.FullPath.ToOSPath())!;
                operation.ApplyInitial(); try { history.PushOperation(operation, participant); } catch { operation.CancelInitial(); throw; }
            }
            assetBrowser.RefreshEntries(workspace!); SolutionExplorerController.RefreshFolders();
            if (preserveIndependentDeletionSelection)
            {
                assetBrowser.RestoreNavigationScope(scope);
                assetBrowser.RestoreContentFolderSelection(folderSelection, folderPrimary);
                assetBrowser.RestoreSelection(selected, primary);
            }
            else
            {
                if (scope.Primary != null && deletingAssetFolders.FirstOrDefault(folder => scope.Primary.Equals(folder.Id, StringComparison.OrdinalIgnoreCase) || scope.Primary.StartsWith(folder.Id + "/", StringComparison.OrdinalIgnoreCase)) is { Parent: { } parent }
                    && assetBrowser.FindFolder(parent.Id) is { } surviving) assetBrowser.SelectFolder(surviving);
                assetBrowser.RestoreSelection([], null);
            }
            var afterSelection = assetBrowser.CaptureSelection(); var afterPrimary = assetBrowser.Primary;
            var afterFolders = assetBrowser.CaptureContentFolderSelection(); var afterFolderPrimary = assetBrowser.PrimaryContentFolder;
            assetDeletionHistory.SetDesiredSelection(afterSelection);
            var afterScope = assetBrowser.CaptureNavigationScope();
            history.PushOperation(new FolderNavigationBoundaryOperation(() => { }, () => { RestoreFolderNavigation(afterScope, afterSelection, afterPrimary, afterFolders, afterFolderPrimary); assetDeletionHistory.SetDesiredSelection(afterSelection); }), navigationParticipant);
        }
        if (history.IsCapturingOperations) Apply();
        else { using var capture = history.CaptureOperationsAtomic(Apply); capture.Publish(history, "Delete selection"); }
    }
    private string AssetContentDeletionMessage()
    {
        if (deletingAssetFolders.Length == 0) return deletingAssets.Length == 1 ? "Are you sure you want to delete this asset?" : "Are you sure you want to delete these " + deletingAssets.Length + " assets?";
        var parts = new List<string>();
        if (deletionExplicitAssetCount != 0) parts.Add(deletionExplicitAssetCount == 1 ? "this asset" : "these " + deletionExplicitAssetCount + " assets");
        if (deletionExplicitFolderCount != 0) parts.Add(deletionExplicitFolderCount == 1 ? "this folder" : "these " + deletionExplicitFolderCount + " folders");
        return "Are you sure you want to delete " + string.Join(" and ", parts) + "?";
    }
}
