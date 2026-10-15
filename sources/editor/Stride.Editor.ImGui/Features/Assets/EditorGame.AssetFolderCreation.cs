using Stride.Core;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private PackageFolderHistory packageFolderHistory = new();
    private bool packageFolderCommandContext;
    private long assetFolderHistoryVersion = -1;
    private AssetBrowserFolder[]? createAssetFolderParents;

    private bool CanCreateAssetFolder(AssetBrowserFolder parent) => CanStartAssetEdit
        && AssetClipboardLocations(parent) is { Length: > 0 } locations
        && locations.All(folder => !folder.IsCode && IsCurrentEditableAssetPackage(folder.Package)
            && assetBrowser.FindFolder(folder.Id) is { } current && ReferenceEquals(current.Package, folder.Package)
            && current.RelativePath == folder.RelativePath && !current.IsCode);

    private void RequestAssetFolderCreation(AssetBrowserFolder parent)
    {
        if (!CanCreateAssetFolder(parent)) throw new InvalidOperationException("Folders can only be created in the Assets hierarchy of an editable package.");
        createAssetFolderParents = AssetClipboardLocations(parent);
    }

    private void DrawAssetFolderCreationDialog()
    {
        // Requests are consumed after panel enumeration, before next frame's inline editor.
        if (createAssetFolderParents is not { } parents) return;
        createAssetFolderParents = null;
        Execute(() => CreateAssetFolders(parents));
    }

    private void CreateAssetFolders(AssetBrowserFolder[] parents)
    {
        if (!CanStartAssetEdit || parents.Length == 0 || parents.Any(folder => folder.IsCode
            || !IsCurrentEditableAssetPackage(folder.Package) || assetBrowser.FindFolder(folder.Id) is not { } current
            || !ReferenceEquals(current.Package, folder.Package) || current.RelativePath != folder.RelativePath || current.IsCode))
            throw new InvalidOperationException("The selected folder is no longer available for editing.");
        var createdIds = new List<string>();
        using (var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            foreach (var parent in parents)
            {
                var name = NamingHelper.ComputeNewName("New folder", candidate => parent.Children.Any(child =>
                    string.Equals(child.Name, candidate.ToString(), StringComparison.OrdinalIgnoreCase)));
                packageFolderHistory.CreateFolder(parent.Package, parent.RelativePath, name);
                createdIds.Add(parent.Id + "/" + name);
            }
        })) capture.Publish(sessionHistory, "Create folder");
        packageFolderCommandContext = true; assetDeletionCommandContext = false;
        assetRelocationCommandContext = assetTagBatchCommandContext = false;
        SolutionExplorerController.RefreshFolders();
        var created = createdIds.Select(assetBrowser.FindFolder).OfType<AssetBrowserFolder>().ToArray();
        if (created.Length != createdIds.Count) throw new InvalidOperationException("The created folder is no longer available.");
        assetBrowser.SelectFolders(created, created[^1], false);
        RequestAssetFolderRename(created[^1]);
        SolutionExplorerController.RevealFolder(created[^1]);
    }
    private void ClearAssetFolderCreation() { createAssetFolderParents = null; packageFolderCommandContext = false; }
}
