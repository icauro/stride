using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class SolutionExplorerPanel
{
    private readonly Dictionary<string, (WeakReference<Package>? Package, bool Expand)> pendingLocationExpansion = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (WeakReference<Package> Package, bool Expand)> pendingDependencyExpansion = new(StringComparer.OrdinalIgnoreCase);
    private bool scrollExpansionTop;
    private void DrawExpansionToolbar()
    {
        Gui.PushID("ExplorerExpansion");
        try
        {
            if (host.DrawExpansionButton("folder-open", "Expand all asset folders")) QueueLocationExpansion(true, true);
            Gui.SameLine();
            if (host.DrawExpansionButton("hierarchy-expand", "Expand all")) QueueLocationExpansion(true, false);
            Gui.SameLine();
            if (host.DrawExpansionButton("hierarchy-collapse", "Collapse all")) QueueLocationExpansion(false, false);
        }
        finally { Gui.PopID(); }
    }
    private void QueueLocationExpansion(bool expand, bool assetsOnly)
    {
        if (host.Workspace == null) return;
        ClearLocationPointer(); locationFocusTarget = locationExpansionTarget = null; keyboardSelection = null;
        if (!expand) revealFolder = null;
        foreach (var category in host.Browser.Categories)
        {
            if (expand) pendingLocationExpansion[category.Id] = (null, true);
            foreach (var root in category.Roots)
            {
                pendingLocationExpansion[root.Id] = (new(root.Package), expand);
                foreach (var child in root.Children)
                    if (!assetsOnly || !child.IsCode) QueueDirectory(child);
                if (!assetsOnly && root.Package.Container is SolutionProject) pendingDependencyExpansion[root.Id] = (new(root.Package), expand);
            }
        }
        scrollExpansionTop = !expand;
        void QueueDirectory(AssetBrowserFolder folder)
        {
            pendingLocationExpansion[folder.Id] = (new(folder.Package), expand);
            foreach (var child in folder.Children) QueueDirectory(child);
        }
    }
    private void PrepareLocationExpansion(AssetBrowserLocation location)
    {
        if (!pendingLocationExpansion.Remove(location.Id, out var pending)) return;
        if (location.Category is { } category ? ReferenceEquals(category.Owner, host.Workspace)
            : pending.Package != null && pending.Package.TryGetTarget(out var package) && ReferenceEquals(location.Folder!.Package, package))
            Gui.SetNextItemOpen(pending.Expand, ImGuiCond.Always);
    }
    private void PrepareDependencyExpansion(AssetBrowserFolder root)
    {
        if (root.Package.Container is SolutionProject && pendingDependencyExpansion.Remove(root.Id, out var pending)
            && pending.Package.TryGetTarget(out var package) && ReferenceEquals(root.Package, package))
            Gui.SetNextItemOpen(pending.Expand, ImGuiCond.Always);
    }
    private void CompleteLocationExpansion()
    {
        if (!scrollExpansionTop) return;
        Gui.SetScrollY(0); scrollExpansionTop = false;
    }
    private void ClearLocationExpansion()
    { pendingLocationExpansion.Clear(); pendingDependencyExpansion.Clear(); scrollExpansionTop = false; }
}
