using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetBrowserFolder? ExplorerRenameLocation()
    {
        if (!CanStartAssetEdit) return null;
        var scope = assetBrowser.CaptureNavigationScope();
        if (scope.Categories.Length != 0 || scope.Locations.Length == 0 || scope.Folders.Length == 0) return null;
        foreach (var id in scope.Locations)
        {
            if (assetBrowser.FindLocation(id)?.Folder is not { } location || !IsCurrentEditableAssetPackage(location.Package)
                || location.Parent != null && location.RelativePath.Length == 0) return null;
        }
        // Original RenameDirectoryOrPackage receives SelectedLocations.LastOrDefault,
        // independent of focused, clicked or range-primary row identity.
        var target = assetBrowser.FindLocation(scope.Locations[^1])?.Folder;
        if (target == null || target.Parent == null && target.Package.Container is SolutionProject) return null;
        return target.Parent == null ? CanRenameStandalonePackage(target) ? target : null
            : CanRelocateAssetFolder(target) ? target : null;
    }
    private void RequestExplorerLocationRename()
    {
        if (ExplorerRenameLocation() is not { } target || workspace is not { } owner) return;
        var expected = new WeakReference<ProjectWorkspace>(owner); var version = assetBrowser.SelectionVersion;
        bool Current() => expected.TryGetTarget(out var current) && ReferenceEquals(workspace, current)
            && assetBrowser.SelectionVersion == version && ReferenceEquals(ExplorerRenameLocation(), target);
        if (target.Parent == null) RequestStandalonePackageRename(target, Current);
        else RequestAssetFolderRename(target, Current);
        if (Current()) SolutionExplorerController.RevealFolder(target);
    }
}
