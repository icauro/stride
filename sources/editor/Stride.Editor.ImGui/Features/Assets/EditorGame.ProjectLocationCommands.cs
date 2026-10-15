namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action? CaptureProjectLocationCommand(Func<bool> eligible, Action command)
    {
        if (workspace is not { } owner || !eligible()) return null;
        var capturedOwner = new WeakReference<ProjectWorkspace>(owner);
        var selectionVersion = assetBrowser.SelectionVersion;
        var scope = assetBrowser.CaptureNavigationScope();
        return () =>
        {
            if (capturedOwner.TryGetTarget(out var current) && ReferenceEquals(workspace, current)
                && assetBrowser.SelectionVersion == selectionVersion
                && assetBrowser.CaptureNavigationScope().SameScope(scope) && eligible()) command();
        };
    }

    private void DrawProjectLocationCommands()
    {
        // GameStudioWindow binds these commands to ActiveAssetView.SelectedLocations,
        // including when selected Asset view content differs from the Explorer scope.
        var folder = assetBrowser.SelectedFolder;
        var creation = folder == null ? null : CaptureProjectLocationCommand(
            () => ReferenceEquals(assetBrowser.FindFolder(folder.Id), folder) && CanCreateAssetFolder(folder),
            () => RequestAssetFolderCreation(folder));
        var deletion = CaptureProjectLocationCommand(() => CanDeleteExplorerLocations || CanRemoveExplorerPackages,
            () => { if (CanRemoveExplorerPackages) RequestExplorerPackageRemoval(); else RequestExplorerLocationDeletion(); });
        var rename = CaptureProjectLocationCommand(() => ExplorerRenameLocation() != null, RequestExplorerLocationRename);
        Action? selected = null;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Create folder", "Menu"), "", false, creation != null)) selected = creation;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "", false, deletion != null)) selected = deletion;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, rename != null)) selected = rename;
        if (selected != null) Execute(selected);
    }
}
