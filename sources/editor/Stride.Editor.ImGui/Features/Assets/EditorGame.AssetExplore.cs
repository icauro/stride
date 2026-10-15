using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action? CaptureAssetLocationExplore()
    {
        if (assetBrowser.SelectedCategory is { } category)
        {
            if (assetBrowser.SelectedContent.Count == 0 && assetBrowser.SelectedLocationCount != 1
                || category.Kind != AssetBrowserCategoryKind.Local || workspace == null || !ReferenceEquals(workspace, category.Owner)
                || workspace.Session.SolutionPath is not { } solution || string.IsNullOrWhiteSpace(solution.ToOSPath())) return null;
            var categoryOwner = workspace; var categoryPath = solution.ToOSPath(); var categoryVersion = assetBrowser.SelectionVersion;
            return () => Execute(() =>
            {
                if (ReferenceEquals(workspace, categoryOwner) && assetBrowser.SelectionVersion == categoryVersion
                    && assetBrowser.FindCategory(category.Id) is { Kind: AssetBrowserCategoryKind.Local } current
                    && ReferenceEquals(current.Owner, categoryOwner) && categoryOwner.Session.SolutionPath is { } actual && SamePath(actual.ToOSPath(), categoryPath))
                    RevealExternalFile(categoryPath);
            });
        }
        var owner = workspace; var location = assetBrowser.SelectedFolder;
        if (owner == null || location == null || !owner.Session.Packages.Contains(location.Package)
            || assetBrowser.SelectedContent.Count == 0 && assetBrowser.SelectedLocationCount != 1) return null;
        var version = assetBrowser.SelectionVersion;
        return () => Execute(() =>
        {
            if (ReferenceEquals(workspace, owner) && assetBrowser.SelectionVersion == version)
                ExploreAssetFolder(location);
        });
    }
    private void ExploreSelectedAssetContent(AssetBrowserContent? fallback = null)
    {
        var content = assetBrowser.SelectedContent.LastOrDefault() ?? fallback;
        if (content?.Asset is { } asset) ExploreAssetEntry(asset);
        else if (content?.Folder is { } folder) ExploreAssetFolder(folder);
        else if (assetBrowser.SelectedCategory != null) CaptureAssetLocationExplore()?.Invoke();
        else if (assetBrowser.SelectedFolder is { } location) ExploreAssetFolder(location);
    }

    private void ExploreAssetEntry(AssetEntry asset)
    {
        if (workspace == null) return;
        var current = workspace.AssetItems.FirstOrDefault(item => item.Id == asset.Id);
        if (current == null || !SamePath(current.FullPath.ToOSPath(), asset.Path))
            throw new InvalidOperationException("The selected asset is no longer available.");
        RevealExternalFile(current.FullPath.ToOSPath(), true);
    }

    private void ExploreSelectedAssetLocation(AssetBrowserFolder fallback)
    {
        if (assetBrowser.IsFolderSelected(fallback) && assetBrowser.SelectedCategory != null) CaptureAssetLocationExplore()?.Invoke();
        else ExploreAssetFolder(assetBrowser.IsFolderSelected(fallback) ? assetBrowser.SelectedFolder ?? fallback : fallback);
    }

    private Action? DrawAssetCategoryActions(AssetBrowserCategory category)
    {
        if (workspace == null || !ReferenceEquals(workspace, category.Owner) || assetBrowser.FindCategory(category.Id) == null) return null;
        var action = CaptureAssetLocationExplore();
        return DrawNativeMenuItem(EditorLocalization.Widget("Show in Explorer", "Menu"), "", false, action != null) ? action : null;
    }

    private void ExploreAssetFolder(AssetBrowserFolder folder)
    {
        if (workspace == null || !workspace.Session.Packages.Contains(folder.Package)
            || assetBrowser.FindFolder(folder.Id) is not { } current || !ReferenceEquals(current.Package, folder.Package)
            || current.RelativePath != folder.RelativePath || current.IsCode != folder.IsCode)
            throw new InvalidOperationException("The selected folder is no longer available.");
        if (folder.Parent == null)
        {
            if (folder.Package.Container is SolutionProject project)
            {
                if (!ReferenceEquals(project.Package, folder.Package) || !workspace.Session.Projects.Contains(project))
                    throw new InvalidOperationException("The selected project is no longer available.");
                // Native ProjectViewModel.ProjectPath differs from PackageViewModel.PackagePath.
                RevealExternalFile(project.FullPath.ToOSPath());
            }
            else RevealExternalFile(folder.Package.FullPath.ToOSPath());
            return;
        }
        var root = folder.Package.RootDirectory.ToOSPath();
        var mount = folder.IsCode ? root : Path.GetFullPath(folder.Package.GetDefaultAssetFolder().ToOSPath(), root);
        var path = Path.GetFullPath(folder.RelativePath.Length == 0 ? mount : Path.Combine(mount, folder.RelativePath));
        StartAssetExplorer(path, false);
    }
}
