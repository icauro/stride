using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class SolutionExplorerPanel
{
    private sealed record LocationContext(WeakReference<ProjectWorkspace> Owner, AssetBrowserLocation Location, long Version);
    private LocationContext? locationContext;

    private void CaptureLocationContext(AssetBrowserLocation location, string nodeId, bool editing = false)
    {
        if (editing || !Gui.IsItemClicked(ImGuiMouseButton.Right) || Gui.GetIO().AppFocusLost
            || host.Workspace is not { } owner || !ReferenceEquals(host.Browser.FindLocation(location.Id), location)) return;
        ClearLocationPointer();
        if (!host.Browser.IsLocationSelected(location))
        {
            // Native TreeView right MouseDown uses SelectSingleItem only for an unselected row.
            // Previous expanded rows include anchors below the currently rendered row.
            var rows = previousFolders.Where(row => ReferenceEquals(host.Browser.FindLocation(row.Id), row))
                .Concat([location]).DistinctBy(row => row.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            ApplyLocationSelection(location, Gui.GetIO().KeyCtrl, Gui.GetIO().KeyShift, rows);
        }
        ImGuiP.SetFocusID(Gui.GetID(nodeId), ImGuiP.GetCurrentWindow());
        locationContext = new(new(owner), location, host.Browser.SelectionVersion);
    }
    private bool IsCurrentLocationContext(LocationContext? context, AssetBrowserLocation location)
        => context != null && ReferenceEquals(context.Location, location) && context.Owner.TryGetTarget(out var owner)
            && ReferenceEquals(owner, host.Workspace) && host.Browser.SelectionVersion == context.Version
            && ReferenceEquals(host.Browser.FindLocation(location.Id), location);
    private bool ValidateLocationContext(AssetBrowserLocation location)
    {
        if (IsCurrentLocationContext(locationContext, location)) return true;
        Gui.CloseCurrentPopup(); return false;
    }
    private Action? CertifyLocationContextAction(Action? action, AssetBrowserLocation location)
    {
        if (action == null) return null;
        var captured = locationContext;
        return () => { if (IsCurrentLocationContext(captured, location)) action(); };
    }
}
