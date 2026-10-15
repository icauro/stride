using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class SolutionExplorerPanel
{
    private WeakReference<ProjectWorkspace>? observedWorkspace;
    internal void ObserveWorkspace()
    {
        var current = host.Workspace;
        if (observedWorkspace != null && observedWorkspace.TryGetTarget(out var previous) && ReferenceEquals(previous, current)) return;
        ResetWorkspace();
        observedWorkspace = current == null ? null : new(current);
    }
    internal void ResetWorkspace()
    {
        observedWorkspace = null; visibleFolders.Clear(); previousFolders = []; folderAnchor = revealFolder = null;
        pendingAction = null; ClearLocationNavigation(); ClearLocationPointer(); ClearLocationExpansion(); locationContext = null; host.ResetWorkspaceTracking();
    }
    private void DrawCategory(AssetBrowserCategory category)
    {
        var location = host.Browser.Location(category);
        visibleFolders.Add(location);
        Gui.PushID(category.Id);
        try
        {
            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (category.Kind == AssetBrowserCategoryKind.Local) flags |= ImGuiTreeNodeFlags.DefaultOpen;
            if (category.Roots.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;
            if (host.Browser.IsCategorySelected(category)) flags |= ImGuiTreeNodeFlags.Selected;
            var revealWithin = revealFolder != null && category.Roots.Any(root => revealFolder == root.Id || revealFolder.StartsWith(root.Id + "/", StringComparison.OrdinalIgnoreCase));
            if (revealWithin)
                Gui.SetNextItemOpen(true, ImGuiCond.Always);
            var keyboardFocus = PrepareLocationNode(location, "##Category");
            var open = Tree.NativeNode("##Category", category.Name, flags, keyboardFocus || revealWithin, out var virtualized);
            CompleteLocationNode(location, open, virtualized);
            if (!virtualized) { CaptureLocationPointer(location); CaptureLocationContext(location, "##Category"); }
            if (!virtualized && Gui.BeginPopupContextItem("Category actions"))
            {
                try
                {
                    if (ValidateLocationContext(location))
                    {
                        var action = host.DrawCategoryActions(category);
                        action = host.DrawProjectActions(location) ?? action;
                        action = host.DrawAssetAdditionAction() ?? action;
                        pendingAction = CertifyLocationContextAction(action, location) ?? pendingAction;
                    }
                }
                finally { Gui.EndPopup(); }
            }
            if (open)
            {
                try { foreach (var root in category.Roots) DrawFolder(root); }
                finally { Gui.TreePop(); }
            }
        }
        finally { Gui.PopID(); }
    }
}
