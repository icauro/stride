using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class SolutionExplorerPanel(ISolutionExplorerHost host)
{
    internal bool Visible = true;
    internal bool FocusRequested;
    internal EditorTreeView Tree { get; } = new()
    {
        MeasureText = host.MeasureTreeText,
        PaintText = host.DrawTreeText,
        SelectNativeText = host.DrawTreeSelectable,
    };
    internal void RefreshFolders() => host.RefreshFolders();
    private string? revealFolder;
    internal void RevealFolder(AssetBrowserFolder folder) { revealFolder = folder.Id; Visible = true; FocusRequested = true; }
    private Action? pendingAction;
    private readonly List<AssetBrowserLocation> visibleFolders = [];
    private AssetBrowserLocation[] previousFolders = [];
    private string? folderAnchor;
    internal bool Draw()
    {
        if (!Visible) { ClearLocationPointer(); return false; }
        if (FocusRequested) { Gui.SetNextWindowFocus(); FocusRequested = false; }
        var docked = false;
        if (Gui.Begin(EditorLocalization.Widget("Solution explorer", "View"), ref Visible))
        {
            docked = Gui.IsWindowDocked();
            if (host.HasWorkspace)
            {
                host.RefreshFolders();
                DrawLocationToolbar();
                BeginLocationPointerFrame();
                BeginLocationNavigation();
                Tree.Begin(); visibleFolders.Clear();
                foreach (var category in host.Browser.Categories) DrawCategory(category);
                ApplyLocationNavigation();
                CompleteLocationPointerFrame();
                previousFolders = visibleFolders.ToArray();
                CompleteLocationExpansion();
                if (host.Browser.SelectedFolder is { } focusedFolder && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
                    && !Gui.IsAnyItemActive() && !Gui.GetIO().WantTextInput && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
                {
                    if (Gui.IsKeyPressed(ImGuiKey.F2) && host.CanRenameFolder(focusedFolder)) pendingAction = () => host.RequestFolderRename(focusedFolder);
                    else if (Gui.IsKeyPressed(ImGuiKey.Delete, false) && host.CanDeleteFolder(focusedFolder)) pendingAction = () => host.RequestFolderDeletion(focusedFolder);
                }
                if (host.Browser.SelectedFolder is { } folder && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
                    && !Gui.IsAnyItemActive() && !Gui.GetIO().WantTextInput && Gui.GetIO().KeyCtrl
                    && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
                {
                    if (Gui.IsKeyPressed(ImGuiKey.C, false) && host.CanCopyFolder(folder)) pendingAction = () => host.CopyFolder(folder);
                    else if (Gui.IsKeyPressed(ImGuiKey.X, false) && host.CanCutFolder(folder)) pendingAction = () => host.CutFolder(folder);
                    else if (Gui.IsKeyPressed(ImGuiKey.V, false) && host.CanPasteFolder(folder)) pendingAction = () => host.PasteFolder(folder);
                }
            }
        }
        ImGuiScopes.EndWindow();
        var action = pendingAction; pendingAction = null;
        if (action != null) host.Execute(action);
        host.ApplyPendingRelocation();
        return docked;
    }

    private void DrawFolder(AssetBrowserFolder folder)
    {
        visibleFolders.Add(host.Browser.Location(folder));
        Gui.PushID(folder.Id);
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (folder.Parent == null) flags |= ImGuiTreeNodeFlags.DefaultOpen;
        if (folder.Children.Count == 0 && folder.Parent != null) flags |= ImGuiTreeNodeFlags.Leaf;
        if (host.Browser.IsFolderSelected(folder)) flags |= ImGuiTreeNodeFlags.Selected;
        if (revealFolder != null && revealFolder.StartsWith(folder.Id + "/", StringComparison.OrdinalIgnoreCase))
            Gui.SetNextItemOpen(true, ImGuiCond.Always);
        var caption = folder.Parent == null
            ? folder.Name + (folder.Package.IsDirty ? "*" : "") + (folder.Package.State < Stride.Core.Assets.PackageState.AssetsReady ? "(Errors)" : "") : folder.Name;
        var renamingFolder = host.IsRenamingFolder(folder);
        var location = host.Browser.Location(folder);
        var keyboardFocus = PrepareLocationNode(location, "##Folder");
        var open = Tree.NativeNode("##Folder", caption, flags, keyboardFocus || renamingFolder || revealFolder == folder.Id, out var virtualized,
            paint: !renamingFolder, minimumHeight: renamingFolder ? Gui.GetFrameHeight() : 0);
        CompleteLocationNode(location, open, virtualized);
        if (virtualized)
        {
            if (open)
            {
                foreach (var child in folder.Children) DrawFolder(child);
                if (folder.Parent == null) { PrepareDependencyExpansion(folder); host.DrawDependencies(folder.Package); }
                Gui.TreePop();
            }
            Gui.PopID(); return;
        }
        var rectangle = Gui.GetItemRectMin(); var rowMaximum = Gui.GetItemRectMax();
        if (revealFolder == folder.Id) { Gui.SetScrollHereY(); revealFolder = null; }
        CaptureLocationPointer(location, renamingFolder);
        CaptureLocationContext(location, "##Folder", renamingFolder);
        host.DrawFolderDragSource(folder);
        host.DrawFolderDropTarget(folder);
        var renameOrigin = new NVector2(rectangle.X + Gui.GetTreeNodeToLabelSpacing(), rectangle.Y + Math.Max(0, (rowMaximum.Y - rectangle.Y - Gui.GetFrameHeight()) / 2));
        var cursor = Gui.GetCursorScreenPos(); Gui.SetCursorScreenPos(renameOrigin);
        host.DrawFolderRenameField(folder); Gui.SetCursorScreenPos(cursor);
        if (Gui.BeginPopupContextItem("Folder actions"))
        {
            if (ValidateLocationContext(location))
            {
                pendingAction = host.DrawProjectActions(location) ?? pendingAction;
                if (folder.Parent == null) host.DrawDependencyActions(folder.Package);
                if (Gui.MenuItem(EditorLocalization.Widget("New folder", "Menu"), "", false, host.CanCreateFolder(folder))) host.RequestFolderCreation(folder);
                pendingAction = host.DrawFolderClipboardActions(folder) ?? pendingAction;
                pendingAction = host.DrawAssetAdditionAction() ?? pendingAction;
                if (Gui.MenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, host.CanRenameFolder(folder))) pendingAction = () => host.RequestFolderRename(folder);
                if (Gui.MenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, host.CanDeleteFolder(folder))) pendingAction = () => host.RequestFolderDeletion(folder);
                if (Gui.MenuItem(EditorLocalization.Widget("Package properties", "Menu")))
                {
                    host.Browser.SelectFolder(folder); host.SelectPackageProperties(folder.Package);
                }
                pendingAction = CertifyLocationContextAction(pendingAction, location);
            }
            Gui.EndPopup();
        }
        if (open)
        {
            foreach (var child in folder.Children) DrawFolder(child);
            if (folder.Parent == null) { PrepareDependencyExpansion(folder); host.DrawDependencies(folder.Package); }
            Gui.TreePop();
        }
        Gui.PopID();
    }
}
