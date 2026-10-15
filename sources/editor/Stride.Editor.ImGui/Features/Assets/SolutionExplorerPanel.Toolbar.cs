using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class SolutionExplorerPanel
{
    private Action? CaptureToolbarFolderAction(Func<AssetBrowserFolder, bool> eligible, Action<AssetBrowserFolder> action)
    {
        if (host.Workspace is not { } owner || host.Browser.SelectedFolder is not { } folder || !eligible(folder)) return null;
        var currentOwner = new WeakReference<ProjectWorkspace>(owner); var version = host.Browser.SelectionVersion;
        return () =>
        {
            if (currentOwner.TryGetTarget(out var current) && ReferenceEquals(host.Workspace, current)
                && host.Browser.SelectionVersion == version && ReferenceEquals(host.Browser.FindFolder(folder.Id), folder) && eligible(folder)) action(folder);
        };
    }
    private void DrawLocationToolbar()
    {
        Gui.PushID("ExplorerLocations");
        try
        {
            var create = CaptureToolbarFolderAction(host.CanCreateFolder, host.RequestFolderCreation);
            if (host.DrawLocationToolbarButton("folder", "Create a folder", create != null)) pendingAction = create;
            Gui.SameLine();
            var rename = CaptureToolbarFolderAction(host.CanRenameFolder, host.RequestFolderRename);
            if (host.DrawLocationToolbarButton("rename", "Rename the selected folder or package", rename != null)) pendingAction = rename;
            Gui.SameLine();
            var deletion = host.CaptureLocationDeletion();
            if (host.DrawLocationToolbarButton("sprite-remove", "Delete the selected items", deletion != null)) pendingAction = deletion;
            Gui.SameLine();
            DrawExpansionToolbar();
            Gui.SameLine();
            var explore = host.CaptureLocationExplore();
            if (host.DrawLocationToolbarButton("sprite-source-explore", "Show in Explorer", explore != null)) pendingAction = explore;
        }
        finally { Gui.PopID(); }
    }
}
