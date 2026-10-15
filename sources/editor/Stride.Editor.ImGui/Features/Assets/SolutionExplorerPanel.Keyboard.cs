using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class SolutionExplorerPanel
{
    private readonly Dictionary<uint, string> locationItemIds = [];
    private readonly HashSet<string> expandedLocations = new(StringComparer.OrdinalIgnoreCase);
    private string? locationFocusTarget, locationExpansionTarget;
    private bool locationExpansionValue;
    private AssetBrowserLocation? keyboardSelection;
    private bool keyboardToggle, keyboardRange;
    private static readonly ImGuiKey[] LocationNavigationKeys = [ImGuiKey.UpArrow, ImGuiKey.DownArrow,
        ImGuiKey.LeftArrow, ImGuiKey.RightArrow, ImGuiKey.Home, ImGuiKey.End, ImGuiKey.Space,
        ImGuiKey.KeypadAdd, ImGuiKey.KeypadSubtract];

    private void BeginLocationNavigation()
    {
        keyboardSelection = null;
        var io = Gui.GetIO();
        if (!io.AppFocusLost && !io.KeyAlt && !io.KeySuper && !io.WantTextInput && !Gui.IsAnyItemActive()
            && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && locationItemIds.TryGetValue(Gui.GetCurrentContext().NavId, out var focusedId))
        {
            var index = Array.FindIndex(previousFolders, location => location.Id == focusedId);
            if (index >= 0 && ReferenceEquals(host.Browser.FindLocation(focusedId), previousFolders[index]))
                ReadLocationKey(previousFolders[index], index);
        }
        locationItemIds.Clear();
    }

    private void ReadLocationKey(AssetBrowserLocation focused, int index)
    {
        var io = Gui.GetIO();
        foreach (var key in LocationNavigationKeys)
        {
            if (!Gui.IsKeyPressed(key)) continue;
            ImGuiP.SetKeyOwner(key, Gui.GetID("ExplorerLocationNavigation"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            var hasChildren = host.Browser.LocationChildren(focused).Count != 0;
            var open = expandedLocations.Contains(focused.Id);
            if (hasChildren && ((key is ImGuiKey.RightArrow or ImGuiKey.KeypadAdd) && !open
                || (key is ImGuiKey.LeftArrow or ImGuiKey.KeypadSubtract) && open))
            {
                locationExpansionTarget = focused.Id;
                locationExpansionValue = key is ImGuiKey.RightArrow or ImGuiKey.KeypadAdd;
                return;
            }
            var target = key switch
            {
                ImGuiKey.UpArrow => index - 1,
                ImGuiKey.DownArrow => index + 1,
                ImGuiKey.LeftArrow => host.Browser.ParentLocation(focused) is { } parent
                    ? Array.FindIndex(previousFolders, location => ReferenceEquals(location, parent)) : -1,
                ImGuiKey.RightArrow => hasChildren && open ? index + 1 : -1,
                ImGuiKey.Home => 0,
                ImGuiKey.End => previousFolders.Length - 1,
                ImGuiKey.Space => index,
                _ => -1,
            };
            if (target < 0 || target >= previousFolders.Length) return;
            var destination = previousFolders[target];
            if (!ReferenceEquals(host.Browser.FindLocation(destination.Id), destination)) return;
            locationFocusTarget = destination.Id;
            // Original native arrows with Control move focus only, including Control+Shift.
            // Home/End/Space call SelectSingleItem, where Shift range precedes Control toggle.
            if (!io.KeyCtrl || key is ImGuiKey.Home or ImGuiKey.End or ImGuiKey.Space)
            { keyboardSelection = destination; keyboardToggle = io.KeyCtrl; keyboardRange = io.KeyShift; }
            return;
        }
    }

    private bool PrepareLocationNode(AssetBrowserLocation location, string nodeId)
    {
        var id = Gui.GetID(nodeId);
        locationItemIds[id] = location.Id;
        PrepareLocationExpansion(location);
        if (locationExpansionTarget == location.Id)
        { Gui.SetNextItemOpen(locationExpansionValue, ImGuiCond.Always); locationExpansionTarget = null; }
        if (locationFocusTarget == location.Id) ImGuiP.SetFocusID(id, ImGuiP.GetCurrentWindow());
        return locationFocusTarget == location.Id;
    }
    private void CompleteLocationNode(AssetBrowserLocation location, bool open, bool virtualized)
    {
        if (!virtualized) host.RecordLocationControl(location.Id, open);
        if (open) expandedLocations.Add(location.Id); else expandedLocations.Remove(location.Id);
        if (!virtualized && locationFocusTarget == location.Id)
        { Gui.SetScrollHereY(); locationFocusTarget = null; }
    }
    private void ApplyLocationNavigation()
    {
        if (keyboardSelection is not { } location) return;
        keyboardSelection = null;
        ApplyLocationSelection(location, keyboardToggle, keyboardRange);
    }
    private void ApplyLocationSelection(AssetBrowserLocation location, bool toggle, bool range, IReadOnlyList<AssetBrowserLocation>? selectionRows = null)
    {
        var rows = selectionRows ?? visibleFolders;
        if (!ReferenceEquals(host.Browser.FindLocation(location.Id), location)
            || !rows.Any(row => ReferenceEquals(row, location))) return;
        var ordered = rows.ToList();
        var anchorIndex = ordered.FindIndex(row => row.Id == folderAnchor && host.Browser.IsLocationSelected(row));
        if (anchorIndex < 0) anchorIndex = ordered.FindIndex(host.Browser.IsLocationSelected);
        var targetIndex = ordered.FindIndex(row => ReferenceEquals(row, location));
        var version = host.Browser.SelectionVersion;
        if (range && anchorIndex >= 0)
        {
            folderAnchor = ordered[anchorIndex].Id;
            host.Browser.SelectLocations(ordered.Skip(Math.Min(anchorIndex, targetIndex))
                .Take(Math.Abs(targetIndex - anchorIndex) + 1), location, false);
        }
        else
        {
            host.Browser.SelectLocations([location], location, toggle);
            folderAnchor = host.Browser.IsLocationSelected(location) ? location.Id : host.Browser.CaptureNavigationScope().PrimaryLocation;
        }
        if (version == host.Browser.SelectionVersion) return;
        host.OnLocationSelectionChanged();
        if (location.Folder is { Parent: null } root && host.Browser.IsFolderSelected(root)) host.SelectPackageProperties(root.Package);
    }
    private void ClearLocationNavigation()
    {
        locationItemIds.Clear(); expandedLocations.Clear(); locationFocusTarget = locationExpansionTarget = null;
        keyboardSelection = null;
    }
}
