using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanRelocateAssetFolder(AssetBrowserFolder folder) => workspace != null && sessionHistory != null && authoring != null
        && IsEditableAssetPackage(folder.Package) && folder.Parent != null && folder.RelativePath.Length > 0
        && projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating
        && !closeRequested && !closePromptOpen && closingDocument == null
        && AssetFolderClipboardItems(folder).All(item => workspace.Assets.FirstOrDefault(entry => entry.Id == item.Id) is { } entry && CanRelocateAsset(entry));
    private void RelocateAssetFolder(AssetBrowserFolder folder, string nextPath, Package? destinationPackage = null)
    {
        if (!CanRelocateAssetFolder(folder)) throw new InvalidOperationException("This folder can't be moved.");
        var targetPackage = destinationPackage ?? folder.Package;
        if (!IsEditableAssetPackage(targetPackage) || !ReferenceEquals(targetPackage.Session, folder.Package.Session) || folder.IsCode && targetPackage.Container is not SolutionProject)
            throw new InvalidOperationException("The target package cannot accept this folder.");
        nextPath = nextPath.Replace('\\', '/').TrimEnd('/');
        if (Path.IsPathRooted(nextPath) || nextPath.Split('/').Any(part => !IsValidAssetLeafName(part))) throw new ArgumentException("Invalid folder name.");
        if (ReferenceEquals(folder.Package, targetPackage) && nextPath.Equals(folder.RelativePath, StringComparison.Ordinal)) return;
        var caseOnly = ReferenceEquals(folder.Package, targetPackage) && nextPath.Equals(folder.RelativePath, StringComparison.OrdinalIgnoreCase);
        if (ReferenceEquals(folder.Package, targetPackage) && nextPath.StartsWith(folder.RelativePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A folder cannot be moved into itself.");
        var targetMount = assetBrowser.Packages.First(root => ReferenceEquals(root.Package, targetPackage)).Children.First(mount => mount.IsCode == folder.IsCode);
        var mountPrefix = targetMount.Id + "/";
        var nextId = mountPrefix + nextPath;
        if (!caseOnly && assetBrowser.FindFolder(nextId) != null) throw new IOException("A folder with this name already exists.");
        var items = AssetFolderClipboardItems(folder); var ids = items.Select(item => item.Id).ToHashSet();
        if (targetPackage.Assets.Any(item => !ids.Contains(item.Id) && item.UnqualifiedUrl.ToString().StartsWith(nextPath + "/", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The destination folder is occupied.");
        var moves = items.Select(item => (Entry: workspace!.Assets.Single(entry => entry.Id == item.Id),
            Url: nextPath + item.UnqualifiedUrl.ToString()[folder.RelativePath.Length..])).ToArray();
        EnsureFolderRelocationReferenceSafety(ids);
        ValidatePendingPropertyStrings();
        var beforeScope = assetBrowser.CaptureNavigationScope(); var beforeAssets = assetBrowser.CaptureSelection(); var beforePrimary = assetBrowser.Primary;
        var beforeContent = assetBrowser.CaptureContentFolderSelection(); var beforeFolderPrimary = assetBrowser.PrimaryContentFolder;
        string? Remap(string? id) => id != null && (id.Equals(folder.Id, StringComparison.OrdinalIgnoreCase) || id.StartsWith(folder.Id + "/", StringComparison.OrdinalIgnoreCase))
            ? nextId + id[folder.Id.Length..] : id;
        var afterScope = beforeScope with { Folders = beforeScope.Folders.Select(id => Remap(id)!).ToArray(), Primary = Remap(beforeScope.Primary) };
        var afterContent = beforeContent.Select(id => Remap(id)!).ToArray();
        var slash = nextPath.LastIndexOf('/'); var targetParentId = slash < 0 ? targetMount.Id : mountPrefix + nextPath[..slash];
        if (beforeContent.Any(id => id.Equals(folder.Id, StringComparison.OrdinalIgnoreCase)) && !afterScope.Folders.Contains(targetParentId, StringComparer.OrdinalIgnoreCase))
            afterScope = afterScope with { Folders = afterScope.Folders.Append(targetParentId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(), Primary = targetParentId };
        var participant = sessionHistory!.Registry.GetOrCreate(folder.Package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(workspace!, folder.Package, sessionHistory));
        var originalDirectories = folder.IsCode && !caseOnly ? new CutSourceDirectoryOperation(this, folder, items) : null;
        FolderCaseRenameOperation[] caseDirectories = caseOnly ? FolderPhysicalDirectories(folder, items).Select(path => new FolderCaseRenameOperation(path,
            Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(nextPath)))).ToArray() : [];
        void Apply()
        {
            sessionHistory.PushOperation(new FolderNavigationBoundaryOperation(() => RestoreFolderNavigation(beforeScope, beforeAssets, beforePrimary, beforeContent, beforeFolderPrimary), () => { }), participant);
            foreach (var operation in caseDirectories)
            { operation.ApplyInitial(); try { sessionHistory.PushOperation(operation, participant); } catch { operation.CancelInitial(); throw; } }
            if (folder.IsCode)
            {
                var project = folder.Package.Container as SolutionProject ?? throw new InvalidOperationException("The code folder requires an actual project.");
                var document = GetProjectBuildSettingsDocument(project, sessionHistory); document.ValidateProjectFileBaseline();
                var targetProject = (SolutionProject)targetPackage.Container;
                var targetDocument = GetProjectBuildSettingsDocument(targetProject, sessionHistory); targetDocument.ValidateProjectFileBaseline();
                var directories = new OwnedSourceDirectoriesOperation(targetPackage.RootDirectory.ToOSPath());
                try
                {
                    foreach (var path in FolderRelativePaths(folder))
                        directories.Ensure(Path.Combine(targetPackage.RootDirectory.ToOSPath(), (nextPath + path[folder.RelativePath.Length..]).Replace('/', Path.DirectorySeparatorChar)));
                }
                catch { directories.CancelInitial(); throw; }
                if (directories.HasDirectories) { try { sessionHistory.PushOperation(directories, participant); } catch { directories.CancelInitial(); throw; } }
                foreach (var include in document.GetProjectItemPaths("Folder").ToArray())
                {
                    var relative = include.Replace('\\', '/').TrimEnd('/');
                    if (relative.Equals(folder.RelativePath, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(folder.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                        targetDocument.TransferProjectItemFrom(document, "Folder", include, (nextPath + relative[folder.RelativePath.Length..]).Replace('/', Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                }
            }
            foreach (var move in moves) RelocateAsset(move.Entry, move.Url, targetPackage);
            if (!folder.IsCode)
            {
                if (ReferenceEquals(folder.Package, targetPackage)) packageFolderHistory.MoveFolder(folder.Package, folder.RelativePath, nextPath);
                else
                {
                    packageFolderHistory.AddFolders(targetPackage, FolderRelativePaths(folder).Select(path => nextPath + path[folder.RelativePath.Length..]).ToArray());
                    packageFolderHistory.RemoveFolder(folder.Package, folder.RelativePath);
                }
            }
            if (originalDirectories is { HasEffect: true })
            { originalDirectories.ApplyInitial(); try { sessionHistory.PushOperation(originalDirectories, participant); } catch { originalDirectories.CancelInitial(); throw; } }
            RestoreFolderNavigation(afterScope, beforeAssets, beforePrimary, afterContent, Remap(beforeFolderPrimary));
            sessionHistory.PushOperation(new FolderNavigationBoundaryOperation(() => { }, () => RestoreFolderNavigation(afterScope, beforeAssets, beforePrimary, afterContent, Remap(beforeFolderPrimary))), participant);
        }
        if (sessionHistory.IsCapturingOperations) Apply();
        else { using var capture = sessionHistory.CaptureOperationsAtomic(Apply); capture.Publish(sessionHistory, "Move folder"); }
        assetRelocationCommandContext = true; assetDeletionCommandContext = assetTagBatchCommandContext = packageFolderCommandContext = false;
    }
    private void RestoreFolderNavigation(AssetBrowserScopeSnapshot scope, AssetId[] assets, AssetId? primary, string[] folders, string? folderPrimary)
    {
        assetBrowser.RefreshEntries(workspace!); SolutionExplorerController.RefreshFolders();
        assetBrowser.RestoreSelection(assets, primary); assetBrowser.RestoreNavigationScope(scope); assetBrowser.RestoreContentFolderSelection(folders, folderPrimary);
        ActivateAssetPropertyContext(); assetReferences.Clear();
    }
    private static IEnumerable<string> FolderRelativePaths(AssetBrowserFolder folder)
    { yield return folder.RelativePath; foreach (var child in folder.Children) foreach (var path in FolderRelativePaths(child)) yield return path; }
    private static string[] FolderPhysicalDirectories(AssetBrowserFolder folder, AssetItem[] items)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var inside = item.UnqualifiedUrl.ToString()[folder.RelativePath.Length..].TrimStart('/');
            var directory = Path.GetDirectoryName(item.FullPath.ToOSPath())!;
            foreach (var _ in inside.Split('/').Skip(1)) directory = Path.GetDirectoryName(directory)!;
            if (Directory.Exists(directory)) paths.Add(directory);
        }
        if (items.Length == 0)
        {
            var mount = folder; while (mount.SourceFolders.Count == 0 && mount.Parent != null) mount = mount.Parent;
            string[] roots = folder.IsCode ? [folder.Package.RootDirectory.ToOSPath()] : mount.SourceFolders.Select(path => Path.GetFullPath(path, folder.Package.RootDirectory.ToOSPath())).ToArray();
            foreach (var root in roots) { var path = Path.Combine(root, folder.RelativePath.Replace('/', Path.DirectorySeparatorChar)); if (Directory.Exists(path)) paths.Add(path); }
        }
        return paths.ToArray();
    }
    private void EnsureFolderRelocationReferenceSafety(HashSet<AssetId> ids)
    {
        var retained = documents.AuthoredDocuments.OfType<IAssetTagDocument>().ToDictionary(document => document.AuthoredAsset.Id, document => document.AuthoredAsset);
        var snapshot = workspace!.Session.DependencyManager.CaptureDependencies(retained);
        var affected = ids.Concat(ids.SelectMany(id => snapshot.GetValueOrDefault(id)?.LinksIn.Where(link => (link.Type & Stride.Core.Assets.Analysis.ContentLinkType.Reference) != 0).Select(link => link.Item.Id) ?? [])).Distinct();
        foreach (var id in affected)
        {
            if (workspace.Assets.FirstOrDefault(entry => entry.Id == id) is not { } entry || typeof(SourceCodeAsset).IsAssignableFrom(entry.AssetType)) continue;
            var document = GetAssetTagDocument(entry, false);
            // Actual owner journals validate sealed/readonly/inherited paths when preparing each URL move.
        }
    }
    private sealed class FolderNavigationBoundaryOperation(Action undo, Action redo) : Operation
    { protected override void Rollback() => undo(); protected override void Rollforward() => redo(); }
    private sealed class FolderCaseRenameOperation(string before, string after) : Operation
    {
        private readonly OwnedDirectoryIdentity identity = WindowsOwnedDirectory.Capture(before);
        private void Move(string from, string to) { using var directory = new WindowsOwnedDirectoryRename(from, identity); directory.Move(to); }
        internal void ApplyInitial() => Move(before, after);
        internal void CancelInitial() => Move(after, before);
        protected override void Rollback() => Move(after, before);
        protected override void Rollforward() => Move(before, after);
    }
}
