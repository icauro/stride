namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetBrowserFolder[] ExplorerDeletionLocations()
    {
        var scope = assetBrowser.CaptureNavigationScope();
        if (!CanStartAssetEdit || scope.Categories.Length != 0 || scope.Folders.Length == 0) return [];
        var folders = scope.Folders.Select(assetBrowser.FindFolder).OfType<AssetBrowserFolder>().ToArray();
        return folders.Length == scope.Folders.Length && folders.All(CanCutAssetFolder) ? folders : [];
    }
    private bool CanDeleteExplorerLocations => ExplorerDeletionLocations().Length != 0;
    private void RequestExplorerLocationDeletion()
    {
        var folders = ExplorerDeletionLocations();
        if (folders.Length == 0 || workspace is not { } owner) return;
        var version = assetBrowser.SelectionVersion;
        var certificates = folders.Select(folder => (folder.Id, folder.Package, folder.RelativePath, folder.IsCode)).ToArray();
        bool Current() => ReferenceEquals(workspace, owner) && assetBrowser.SelectionVersion == version
            && certificates.All(certificate => assetBrowser.FindFolder(certificate.Id) is { } actual
                && ReferenceEquals(actual.Package, certificate.Package) && actual.RelativePath == certificate.RelativePath
                && actual.IsCode == certificate.IsCode && CanCutAssetFolder(actual));
        // This actual independent browser selection supplies the native deletion collector;
        // it never changes the Explorer/main content selection before confirmation.
        var selection = new AssetBrowserState(); selection.Bind(owner);
        foreach (var group in folders.Where(folder => folder.IsCode).GroupBy(folder => folder.Package))
            selection.AddProjectFolders(group.Key, group.Select(folder => folder.RelativePath));
        selection.RestoreContentFolderSelection(folders.Select(folder => folder.Id), folders.Last().Id);
        if (selection.SelectedContentFolders.Count != folders.Length)
            throw new InvalidOperationException("The selected Explorer folders are no longer available for deletion.");
        RequestAssetDeletion(selection, Current);
    }
}
