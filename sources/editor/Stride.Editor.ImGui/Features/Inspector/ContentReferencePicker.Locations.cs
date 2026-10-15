using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private readonly List<AssetBrowserLocation> modalVisibleLocations = [];
    private string? modalLocationAnchor;
    private AssetBrowserLocation? modalClickedLocation;
    private bool modalLocationToggle, modalLocationRange;
    private readonly Dictionary<uint, string> modalLocationItemIds = [];
    private string? modalLocationFocusTarget, modalLocationExpansionTarget;
    private bool modalLocationExpansionValue, modalLocationConsumedEscape;

    private static readonly ImGuiKey[] PickerLocationKeys = [ImGuiKey.UpArrow, ImGuiKey.DownArrow,
        ImGuiKey.LeftArrow, ImGuiKey.RightArrow, ImGuiKey.Home, ImGuiKey.End, ImGuiKey.Space,
        ImGuiKey.KeypadAdd, ImGuiKey.KeypadSubtract, ImGuiKey.Enter, ImGuiKey.Escape];

    private void BeginPickerLocations(ModalRequest request)
    {
        modalClickedLocation = null;
        modalLocationConsumedEscape = false;
        ReadPickerLocationNavigation(request);
        modalVisibleLocations.Clear();
        modalLocationItemIds.Clear();
    }

    private void ReadPickerLocationNavigation(ModalRequest request)
    {
        var io = Gui.GetIO();
        if (!request.IsCurrent() || io.AppFocusLost || io.KeyAlt || io.KeySuper || io.WantTextInput || Gui.IsAnyItemActive()
            || !Gui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows)
            || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)
            || !modalLocationItemIds.TryGetValue(Gui.GetCurrentContext().NavId, out var focusedId)) return;
        var index = modalVisibleLocations.FindIndex(folder => folder.Id == focusedId);
        if (index < 0 || !ReferenceEquals(modalBrowser.FindLocation(focusedId), modalVisibleLocations[index])) return;
        var focused = modalVisibleLocations[index];
        foreach (var key in PickerLocationKeys)
        {
            if (!Gui.IsKeyPressed(key)) continue;
            ImGuiP.SetKeyOwner(key, Gui.GetID("PickerLocationNavigation"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            if (key is ImGuiKey.Enter or ImGuiKey.Escape)
            { modalLocationConsumedEscape = key == ImGuiKey.Escape; return; }
            var hasChildren = modalBrowser.LocationChildren(focused).Count != 0;
            var open = expandedFolders.Contains(focused.Id);
            if (hasChildren && ((key is ImGuiKey.RightArrow or ImGuiKey.KeypadAdd) && !open
                || (key is ImGuiKey.LeftArrow or ImGuiKey.KeypadSubtract) && open))
            {
                modalLocationExpansionTarget = focused.Id;
                modalLocationExpansionValue = key is ImGuiKey.RightArrow or ImGuiKey.KeypadAdd;
                return;
            }
            if (key is ImGuiKey.KeypadAdd or ImGuiKey.KeypadSubtract) return;
            var target = key switch
            {
                ImGuiKey.UpArrow => index - 1,
                ImGuiKey.DownArrow => index + 1,
                ImGuiKey.LeftArrow => modalBrowser.ParentLocation(focused) is { } parent
                    ? modalVisibleLocations.FindIndex(folder => ReferenceEquals(folder, parent)) : -1,
                ImGuiKey.RightArrow => hasChildren && open ? index + 1 : -1,
                ImGuiKey.Home => 0,
                ImGuiKey.End => modalVisibleLocations.Count - 1,
                ImGuiKey.Space => index,
                _ => -1,
            };
            if (target < 0 || target >= modalVisibleLocations.Count) return;
            var destination = modalVisibleLocations[target];
            modalLocationFocusTarget = destination.Id;
            // Native Ctrl arrows move focus only; Home/End/Space still route SelectSingleItem.
            if (!io.KeyCtrl || key is ImGuiKey.Home or ImGuiKey.End or ImGuiKey.Space)
            {
                modalClickedLocation = destination;
                modalLocationToggle = io.KeyCtrl;
                modalLocationRange = io.KeyShift;
            }
            return;
        }
    }

    private void PreparePickerLocationNode(AssetBrowserLocation folder)
    {
        var id = Gui.GetID("##PickerFolder");
        modalLocationItemIds[id] = folder.Id;
        if (modalLocationExpansionTarget == folder.Id)
        {
            Gui.SetNextItemOpen(modalLocationExpansionValue, ImGuiCond.Always);
            modalLocationExpansionTarget = null;
        }
        if (modalLocationFocusTarget == folder.Id) ImGuiP.SetFocusID(id, ImGuiP.GetCurrentWindow());
    }

    private void CompletePickerLocationNode(AssetBrowserLocation folder, bool virtualized)
    {
        if (!virtualized && modalLocationFocusTarget == folder.Id)
        { Gui.SetScrollHereY(); modalLocationFocusTarget = null; }
    }

    private void CapturePickerLocationClick(AssetBrowserLocation folder)
    {
        if (Gui.GetIO().AppFocusLost) return;
        modalClickedLocation = folder;
        modalLocationToggle = Gui.GetIO().KeyCtrl;
        modalLocationRange = Gui.GetIO().KeyShift;
    }

    private void ApplyPickerLocationClick(ModalRequest request)
    {
        if (modalClickedLocation is not { } clicked || !request.IsCurrent()) return;
        modalClickedLocation = null;
        var current = modalBrowser.FindLocation(clicked.Id);
        if (!ReferenceEquals(current, clicked)) return;
        var anchorIndex = modalVisibleLocations.FindIndex(folder => folder.Id == modalLocationAnchor);
        if (anchorIndex < 0 || !modalBrowser.IsLocationSelected(modalVisibleLocations[anchorIndex]))
            anchorIndex = modalVisibleLocations.FindIndex(modalBrowser.IsLocationSelected);
        var targetIndex = modalVisibleLocations.FindIndex(folder => ReferenceEquals(folder, clicked));
        if (modalLocationRange && anchorIndex >= 0 && targetIndex >= 0)
        {
            modalLocationAnchor = modalVisibleLocations[anchorIndex].Id;
            modalBrowser.SelectLocations(modalVisibleLocations.Skip(Math.Min(anchorIndex, targetIndex))
                .Take(Math.Abs(targetIndex - anchorIndex) + 1), clicked, false);
        }
        else
        {
            modalBrowser.SelectLocations([clicked], clicked, modalLocationToggle);
            modalLocationAnchor = modalBrowser.IsLocationSelected(clicked) ? clicked.Id : modalBrowser.CaptureNavigationScope().PrimaryLocation;
        }
    }
}
