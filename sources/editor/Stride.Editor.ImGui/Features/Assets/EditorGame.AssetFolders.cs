using Stride.Core.Assets;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SolutionExplorerPanel? ownedSolutionExplorer;
    private SolutionExplorerHost? ownedSolutionExplorerHost;
    private SolutionExplorerPanel SolutionExplorerController => ownedSolutionExplorer ??= new(ownedSolutionExplorerHost ??= new(this));
    private (bool Failed, long RetryAt, string? Error, int Attempts, bool Published, long Revision) InspectProjectFolderRead(Package package, string path)
        => ownedSolutionExplorerHost?.InspectProjectRead(package, path) ?? default;
    private (long Calls, int LastFrame) InspectProjectFolderRefresh()
        => ownedSolutionExplorerHost?.InspectRefresh() ?? default;
    private EditorTreeView assetFolderTree => SolutionExplorerController.Tree;
    private void ClearSolutionExplorerWorkspace()
    { ownedSolutionExplorer?.ResetWorkspace(); ClearStandalonePackageRename(); ClearAssetFolderRename(); }
    private void DrawSolutionExplorerPanel()
    {
        SolutionExplorerController.ObserveWorkspace();
        if (!SolutionExplorerController.Visible) return;
        var hierarchyWindow = ImGuiP.FindWindowByName(EditorPanels.Hierarchy.DisplayWindowName);
        if (!hierarchyWindow.IsNull && hierarchyWindow.DockId != 0)
            Gui.SetNextWindowDockID(hierarchyWindow.DockId, ImGuiCond.FirstUseEver);
        if (SolutionExplorerController.Draw()) dockedPanels++;
    }

    private sealed class SolutionExplorerHost(EditorGame editor) : ISolutionExplorerHost
    {
        public bool HasWorkspace => editor.workspace != null;
        public void RecordLocationControl(string id, bool open)
        {
            if (!editor.Controls.Enabled) return;
            editor.Controls.Record("location/" + id);
            editor.Controls.Expanded["location/" + id] = open;
            var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
            var point = new System.Numerics.Vector2(minimum.X + Gui.GetTreeNodeToLabelSpacing() / 2,
                (minimum.Y + maximum.Y) / 2);
            var window = ImGuiP.GetCurrentWindowRead();
            var key = "location-expand/" + id;
            if (!window.IsNull && point.X >= window.ClipRect.Min.X && point.X < window.ClipRect.Max.X
                && point.Y >= window.ClipRect.Min.Y && point.Y < window.ClipRect.Max.Y)
            {
                editor.Controls.Centers[key] = point; editor.Controls.Viewports[key] = Gui.GetWindowViewport().ID;
                editor.Controls.NativeIDs[key] = Gui.GetItemID(); editor.Controls.NativeFrames[key] = Gui.GetFrameCount();
            }
            else { editor.Controls.Centers.Remove(key); editor.Controls.Viewports.Remove(key); editor.Controls.NativeIDs.Remove(key); editor.Controls.NativeFrames.Remove(key); }
        }
        public ProjectWorkspace? Workspace => editor.workspace;
        public void ResetWorkspaceTracking() { projectFolderRevisions.Clear(); projectFolderFailures.Clear(); editor.assetFolderHistoryVersion = -1; }
        public AssetBrowserState Browser => editor.assetBrowser;
        public bool DrawExpansionButton(string icon, string tooltip) => editor.HierarchyIconButton(icon, EditorLocalization.Text(tooltip, "ToolTip"));
        public bool DrawLocationToolbarButton(string icon, string tooltip, bool enabled)
        {
            Gui.BeginDisabled(!enabled);
            try { return editor.HierarchyIconButton(icon, EditorLocalization.Text(tooltip, "ToolTip")); }
            finally { Gui.EndDisabled(); }
        }
        public Action? CaptureLocationDeletion()
        {
            if ((!editor.CanDeleteExplorerLocations && !editor.CanRemoveExplorerPackages) || editor.workspace is not { } owner) return null;
            var expected = new WeakReference<ProjectWorkspace>(owner); var version = editor.assetBrowser.SelectionVersion;
            return () =>
            {
                if (expected.TryGetTarget(out var current) && ReferenceEquals(editor.workspace, current)
                    && editor.assetBrowser.SelectionVersion == version)
                { if (editor.CanRemoveExplorerPackages) editor.RequestExplorerPackageRemoval(); else if (editor.CanDeleteExplorerLocations) editor.RequestExplorerLocationDeletion(); }
            };
        }
        public Action? CaptureLocationExplore() => editor.CaptureAssetLocationExplore();
        private readonly Dictionary<Package, long> projectFolderRevisions = [];
        private readonly Dictionary<string, (long RetryAt, string Error, int Attempts)> projectFolderFailures = new(StringComparer.OrdinalIgnoreCase);
        private long folderRefreshCalls;
        private int lastFolderRefreshFrame;
        internal (long Calls, int LastFrame) InspectRefresh() => (folderRefreshCalls, lastFolderRefreshFrame);
        internal (bool Failed, long RetryAt, string? Error, int Attempts, bool Published, long Revision) InspectProjectRead(Package package, string path)
        {
            var failed = projectFolderFailures.TryGetValue(path, out var failure);
            var published = projectFolderRevisions.TryGetValue(package, out var revision);
            return (failed, failure.RetryAt, failure.Error, failure.Attempts, published, revision);
        }
        public void RefreshFolders()
        {
            folderRefreshCalls++; lastFolderRefreshFrame = Gui.GetFrameCount();
            if (editor.workspace == null || editor.sessionHistory == null) return;
            var projects = new List<(Package Package, ProjectBuildSettingsDocument Document)>();
            foreach (var package in editor.workspace.Session.LocalPackages)
            {
                if (package.IsReadOnly || package.Container is not SolutionProject project) continue;
                var path = project.FullPath.ToOSPath();
                var now = Environment.TickCount64;
                if (projectFolderFailures.TryGetValue(path, out var previous) && now < previous.RetryAt) continue;
                try
                {
                    projects.Add((package, GetProjectBuildSettingsDocument(project, editor.sessionHistory)));
                    projectFolderFailures.Remove(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    var diagnostic = error.ToString();
                    if (previous.Error != diagnostic)
                        editor.outputLog.Append("Assets", Stride.Core.Diagnostics.LogMessageType.Error, path,
                            error.Message, diagnostic, exceptionType: error.GetType().FullName);
                    projectFolderFailures[path] = (now + 1000, diagnostic, previous.Attempts + 1);
                }
            }
            if (projectFolderFailures.Count != 0)
            {
                var livePaths = editor.workspace.Session.LocalPackages.Where(package => package.Container is SolutionProject)
                    .Select(package => ((SolutionProject)package.Container).FullPath.ToOSPath()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in projectFolderFailures.Keys.Where(path => !livePaths.Contains(path)).ToArray()) projectFolderFailures.Remove(path);
            }
            if (editor.assetFolderHistoryVersion == editor.packageFolderHistory.Version && projects.Count == projectFolderRevisions.Count
                && projects.All(pair => projectFolderRevisions.TryGetValue(pair.Package, out var revision) && revision == pair.Document.AuthoringRevision)) return;
            editor.assetBrowser.RefreshFolders(editor.workspace!);
            editor.assetFolderHistoryVersion = editor.packageFolderHistory.Version;
            projectFolderRevisions.Clear();
            foreach (var pair in projects)
            {
                editor.assetBrowser.AddProjectFolders(pair.Package, pair.Document.GetProjectItemPaths("Folder"));
                projectFolderRevisions[pair.Package] = pair.Document.AuthoringRevision;
            }
        }
        public void SelectPackageProperties(Package package) => editor.RequestPackageProperties(package);
        public void OnLocationSelectionChanged()
        {
            editor.ActivateAssetPropertyContext();
            if (editor.assetBrowser.SelectedCount != 1 || editor.assetBrowser.SelectedContentFolders.Count != 0) editor.ClearSelectedAssetPreview();
        }
        public Action? DrawCategoryActions(AssetBrowserCategory category) => editor.DrawAssetCategoryActions(category);
        public Action? DrawAssetAdditionAction()
        {
            if (editor.CanAddAsset && editor.DrawNativeMenuItem(EditorLocalization.Widget("Add asset...", "Menu"), "Ctrl+I")) return editor.RequestAssetCreation;
            return null;
        }
        public Action? DrawProjectActions(AssetBrowserLocation location) => editor.DrawAssetLocationProjectActions(location);
        public void DrawFolderDropTarget(AssetBrowserFolder folder) => editor.DrawAssetFolderDropTarget(folder);
        public void DrawDependencyActions(Package package) => editor.DrawPackageDependencyFolderActions(package);
        public bool CanCreateFolder(AssetBrowserFolder folder) => editor.CanCreateAssetFolder(folder);
        public void RequestFolderCreation(AssetBrowserFolder folder) => editor.RequestAssetFolderCreation(folder);
        public void DrawDependencies(Package package) => editor.DrawPackageDependencyExplorer(package);
        public Action? DrawFolderClipboardActions(AssetBrowserFolder folder) => editor.DrawAssetFolderClipboardActions(folder);
        public void Execute(Action action) => editor.Execute(action);
        public bool CanRenameFolder(AssetBrowserFolder folder) => editor.ExplorerRenameLocation() != null;
        public bool CanDeleteFolder(AssetBrowserFolder folder) => (!editor.assetBrowser.IsFolderSelected(folder) || editor.assetBrowser.CaptureNavigationScope().Categories.Length == 0) && (editor.CanDeleteExplorerLocations || editor.CanRemoveExplorerPackages);
        public void RequestFolderRename(AssetBrowserFolder folder) => editor.RequestExplorerLocationRename();
        public void RequestFolderDeletion(AssetBrowserFolder folder)
        { if (editor.CanRemoveExplorerPackages) editor.RequestExplorerPackageRemoval(); else editor.RequestExplorerLocationDeletion(); }
        public bool DrawFolderRenameField(AssetBrowserFolder folder, float width = -1) => folder.Parent == null
            ? editor.DrawStandalonePackageRenameField(folder, width) : editor.DrawAssetFolderRenameField(folder, width);
        public bool IsRenamingFolder(AssetBrowserFolder folder) => editor.assetFolderRename.Id == folder.Id || editor.packageRename.Id == folder.Id;
        public System.Numerics.Vector2 MeasureTreeText(string text) => editor.MeasureNativeLabel(text);
        public void DrawTreeText(string text, System.Numerics.Vector2 origin, System.Numerics.Vector2 minimum, System.Numerics.Vector2 maximum)
            => editor.DrawNativeTreeText(text, origin, minimum, maximum);
        public bool DrawTreeSelectable(string label, bool selected) => editor.DrawNativeSelectable(label, selected);
        public void DrawFolderDragSource(AssetBrowserFolder folder) => editor.DrawAssetFolderDragSource(folder);
        public void ApplyPendingRelocation() => editor.ApplyPendingAssetRelocation();
        public bool CanCopyFolder(AssetBrowserFolder folder) => editor.CanCopyAssetLocations(folder);
        public void CopyFolder(AssetBrowserFolder folder) => editor.CopyAssetLocations(folder);
        public bool CanCutFolder(AssetBrowserFolder folder) => (!editor.assetBrowser.IsFolderSelected(folder) || editor.assetBrowser.CaptureNavigationScope().Categories.Length == 0) && editor.CanCutAssetLocations(folder);
        public void CutFolder(AssetBrowserFolder folder) => editor.CutAssetLocations(folder);
        public bool CanPasteFolder(AssetBrowserFolder folder) => editor.CanPasteAssetLocation(folder);
        public void PasteFolder(AssetBrowserFolder folder) => editor.PasteAssetLocation(folder);
    }
}
