using Stride.Core.Assets;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetBrowserFolder[] SelectedAssetContentRoots()
    {
        var folders = assetBrowser.SelectedContentFolders;
        return folders.Where(folder => !folders.Any(other => !ReferenceEquals(folder, other) && ReferenceEquals(folder.Package, other.Package)
            && folder.IsCode == other.IsCode && folder.RelativePath.StartsWith(other.RelativePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    private bool CanCopyAssetContent => workspace != null && authoring != null && SelectedAssetContentRoots() is { Length: > 0 } roots
        && roots.All(CanCopyAssetFolder)
        && assetBrowser.SelectedEntries.All(entry => CanCopyAssetItem(workspace.FindAsset(entry.Id)));
    private bool CanCutAssetContent => CanCopyAssetContent && !closeRequested && !closePromptOpen && closingDocument == null && assetPreviewBuild == null && SelectedAssetContentRoots().All(CanCutAssetFolder)
        && assetBrowser.SelectedEntries.All(entry => workspace!.FindAsset(entry.Id).Asset is SourceCodeAsset ? CanCutSourceAsset(entry) : CanDeleteAsset(entry) || CanCutVisualScript(entry));
    private string SerializeAssetContentClipboard(AssetBrowserFolder[] roots, out AssetItem[] items, IReadOnlyList<AssetEntry>? selection = null)
    {
        documents.CommitAll();
        var collected = new Dictionary<AssetId, AssetItem>(); var locations = new Dictionary<AssetId, UFile>(); var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var codePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in roots)
        {
            var parent = folder.Parent?.RelativePath ?? ""; var prefix = parent.Length == 0 ? "" : parent.TrimEnd('/') + "/";
            foreach (var item in AssetFolderClipboardItems(folder))
            { collected[item.Id] = item; locations[item.Id] = new UFile(item.UnqualifiedUrl.ToString()[prefix.Length..]); }
            void Collect(AssetBrowserFolder current)
            {
                if (current.IsCode != folder.IsCode) return;
                if (current.RelativePath.Length > prefix.Length) (folder.IsCode ? codePaths : assetPaths).Add(current.RelativePath[prefix.Length..]);
                foreach (var child in current.Children) Collect(child);
            }
            Collect(folder);
        }
        foreach (var entry in selection ?? assetBrowser.SelectedEntries)
        {
            if (collected.ContainsKey(entry.Id)) continue;
            var item = workspace!.FindAsset(entry.Id); collected[item.Id] = item;
            locations[item.Id] = new UFile(Path.GetFileName(item.UnqualifiedUrl.ToString()));
        }
        items = collected.Values.ToArray();
        var text = SerializeAssetClipboard(items, locations);
        return assetPaths.Count + codePaths.Count == 0 ? text : AssetFolderClipboardMetadata.Attach(text, assetPaths, codePaths);
    }
    private void CopyAssetContent()
    {
        if (!CanCopyAssetContent) return;
        var text = SerializeAssetContentClipboard(SelectedAssetContentRoots(), out _);
        if (text.Length != 0) Gui.SetClipboardText(text);
    }
    private void CutAssetContent()
    {
        if (!CanCutAssetContent) return;
        CutAssetFolderRoots(SelectedAssetContentRoots(), assetBrowser.SelectedEntries);
    }
    private void CutAssetFolderRoots(AssetBrowserFolder[] roots, IReadOnlyList<AssetEntry> selection)
    {
        if (!CanStartAssetEdit || roots.Any(folder => !CanCutAssetFolder(folder))) throw new InvalidOperationException("This folder selection can't be cut.");
        var directories = roots.Where(folder => folder.IsCode).ToDictionary(folder => folder.Id, folder => new CutSourceDirectoryOperation(this, folder, AssetFolderClipboardItems(folder)));
        var text = SerializeAssetContentClipboard(roots, out var items, selection);
        var entries = items.Select(item => workspace!.Assets.Single(entry => entry.Id == item.Id)).ToArray();
        var previousClipboard = Gui.GetClipboardTextS();
        Gui.SetClipboardText(text);
        if (Gui.GetClipboardTextS() != text) throw new InvalidOperationException("The selection could not be written to the clipboard.");
        try
        {
            using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
            {
                CutAssetEntries(entries, false);
                foreach (var folder in roots)
                {
                    if (!folder.IsCode) { packageFolderHistory.RemoveFolder(folder.Package, folder.RelativePath); continue; }
                    var project = folder.Package.Container as SolutionProject ?? throw new InvalidOperationException("The code folder requires an actual project.");
                    var document = GetProjectBuildSettingsDocument(project, sessionHistory); document.ValidateProjectFileBaseline();
                    foreach (var include in document.GetProjectItemPaths("Folder"))
                    {
                        var relative = include.Replace('\\', '/').TrimEnd('/');
                        if (relative.Equals(folder.RelativePath, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(folder.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                            document.ChangeProjectItem("Folder", include, false);
                    }
                    var operation = directories[folder.Id];
                    if (!operation.HasEffect) continue;
                    operation.ApplyInitial();
                    try { sessionHistory.PushOperation(operation, authoring!.Find(project.FullPath.ToOSPath())!); }
                    catch { operation.CancelInitial(); throw; }
                }
            });
            capture.Publish(sessionHistory, "Cut selection");
        }
        catch { if (Gui.GetClipboardTextS() == text) Gui.SetClipboardText(previousClipboard); throw; }
        assetBrowser.RefreshEntries(workspace!); assetBrowser.RefreshFolders(workspace!);
    }
}
