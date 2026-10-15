using Stride.Core.Assets;
using Stride.Core.Transactions;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<string, CutSourceDirectoryOperation> cutSourceDirectories = new(StringComparer.OrdinalIgnoreCase);
    private bool CanCutAssetFolder(AssetBrowserFolder folder)
        => CanCopyAssetFolder(folder) && IsEditableAssetPackage(folder.Package) && folder.RelativePath.Length != 0 && sessionHistory != null
        && projectLoading == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating
        && AssetFolderClipboardItems(folder).All(item => workspace!.Assets.FirstOrDefault(entry => entry.Id == item.Id) is { } entry
            && (item.Asset is SourceCodeAsset ? CanCutSourceAsset(entry) : CanDeleteAsset(entry) || CanCutVisualScript(entry)));
    private void CutAssetFolder(AssetBrowserFolder folder)
    {
        if (!CanCutAssetFolder(folder)) return;
        var items = AssetFolderClipboardItems(folder);
        var entries = items.Select(item => workspace!.Assets.Single(entry => entry.Id == item.Id)).ToArray();
        var directories = folder.IsCode ? new CutSourceDirectoryOperation(this, folder, items) : null;
        var text = SerializeAssetFolderClipboard(folder);
        Gui.SetClipboardText(text);
        if (Gui.GetClipboardTextS() != text) throw new InvalidOperationException("The folder could not be written to the clipboard.");
        var package = folder.Package;
        using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            CutAssetEntries(entries, false);
            if (folder.IsCode)
            {
                var project = package.Container as SolutionProject ?? throw new InvalidOperationException("The code folder requires an actual project.");
                var document = GetProjectBuildSettingsDocument(project, sessionHistory); document.ValidateProjectFileBaseline();
                foreach (var include in document.GetProjectItemPaths("Folder"))
                {
                    var relative = include.Replace('\\', '/').TrimEnd('/');
                    if (relative.Equals(folder.RelativePath, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(folder.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                        document.ChangeProjectItem("Folder", include, false);
                }
                if (directories!.HasEffect)
                {
                    directories.ApplyInitial();
                    try { sessionHistory.PushOperation(directories, authoring!.Find(project.FullPath.ToOSPath())!); }
                    catch { directories.CancelInitial(); throw; }
                }
            }
            else packageFolderHistory.RemoveFolder(package, folder.RelativePath);
        });
        capture.Publish(sessionHistory, "Cut folder"); assetBrowser.RefreshEntries(workspace!); assetBrowser.RefreshFolders(workspace!);
    }
    private void FinalizeCutSourceDirectories()
    { foreach (var operation in cutSourceDirectories.Values.ToArray()) operation.RemoveSavedDirectories(); }
    private void ClearCutSourceDirectories()
    {
        if (cutSourceDirectories.Values.Any(operation => operation.Pending)) throw new InvalidOperationException("Save or restore removed code folders before closing the session.");
        cutSourceDirectories.Clear();
    }
    private sealed class CutSourceDirectoryOperation : Operation
    {
        private readonly EditorGame game;
        private readonly ProjectWorkspace workspace;
        private readonly EditorSessionHistory history;
        private readonly Package package;
        private readonly string packageRoot, root;
        private readonly OwnedDirectoryPhaseReceipts directories;
        private bool excluded, frozen;
        internal bool Pending => excluded && directories.HasPresentDirectories;
        public override bool HasEffect => directories.HasDirectories;
        internal CutSourceDirectoryOperation(EditorGame game, AssetBrowserFolder folder, AssetItem[] items)
        {
            this.game = game;
            workspace = game.workspace ?? throw new InvalidOperationException("The code folder requires its actual workspace.");
            history = game.sessionHistory ?? throw new InvalidOperationException("The code folder requires its actual history.");
            package = folder.Package;
            packageRoot = Path.GetFullPath(package.RootDirectory.ToOSPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(packageRoot, folder.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
            if (!root.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("The code folder is outside its project.");
            directories = new(packageRoot);
            ValidateContext();
            var files = items.Select(item => Path.GetFullPath(item.FullPath.ToOSPath())).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Where(item => item.Asset is IProjectFileGeneratorAsset)) files.Add(Path.GetFullPath(item.GetGeneratedAbsolutePath().ToOSPath()));
            void Capture(string path)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("The code folder path is occupied.");
                directories.RetainExisting(path, WindowsOwnedDirectory.Capture(path));
                foreach (var file in Directory.EnumerateFiles(path)) if (!files.Contains(Path.GetFullPath(file))) throw new IOException("The code folder contains a file outside the selected authored assets.");
                foreach (var child in Directory.EnumerateDirectories(path)) Capture(child);
            }
            Capture(root);
        }
        private void ValidateContext()
        {
            if (!ReferenceEquals(game.workspace, workspace) || !ReferenceEquals(game.sessionHistory, history)
                || !workspace.Session.Packages.Contains(package) || !IsEditableAssetPackage(package)
                || !string.Equals(packageRoot, Path.GetFullPath(package.RootDirectory.ToOSPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The code folder owner changed before its directory operation.");
        }
        internal void ApplyInitial()
        {
            ValidateContext();
            directories.ValidateOwnedDirectories();
            if (directories.HasAbsentDirectories) throw new IOException("The code folder has unfinished directory restoration.");
            excluded = true; game.cutSourceDirectories[root] = this;
        }
        internal void CancelInitial()
        {
            if (directories.HasAbsentDirectories) throw new IOException("The code folder has unfinished directory restoration.");
            excluded = false;
            if (ReferenceEquals(game.cutSourceDirectories.GetValueOrDefault(root), this)) game.cutSourceDirectories.Remove(root);
        }
        internal void RemoveSavedDirectories()
        {
            if (!Pending) return;
            ValidateContext();
            // The same Cut/save operation owns all native directory receipts; a
            // failed compensation keeps its successful subset for the next retry.
            directories.UndoOwnedDirectories();
            if (frozen && ReferenceEquals(game.cutSourceDirectories.GetValueOrDefault(root), this)) game.cutSourceDirectories.Remove(root);
        }
        protected override void Rollback()
        {
            ValidateContext();
            directories.RedoOwnedDirectories();
            CancelInitial();
        }
        protected override void Rollforward() => ApplyInitial();
        protected override void FreezeContent()
        {
            frozen = true;
            if (!Pending && ReferenceEquals(game.cutSourceDirectories.GetValueOrDefault(root), this)) game.cutSourceDirectories.Remove(root);
        }
    }
}
