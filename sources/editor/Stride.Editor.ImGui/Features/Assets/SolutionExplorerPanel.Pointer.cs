using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class SolutionExplorerPanel
{
    private AssetBrowserLocation? pointerOrigin, pointerSelection;
    private WeakReference<ProjectWorkspace>? pointerWorkspace;
    private long pointerSelectionVersion;
    private bool pointerDragged, pointerToggle, pointerRange;
    private string? lastPointerPressedId, lastPointerAcceptedId;
    private int lastPointerPressedFrame = -1, lastPointerAcceptedFrame = -1, lastPointerCanceledFrame = -1;
    internal (string? PressedId, int PressedFrame, string? AcceptedId, int AcceptedFrame, int CanceledFrame) InspectPointerAcceptance()
        => (lastPointerPressedId, lastPointerPressedFrame, lastPointerAcceptedId, lastPointerAcceptedFrame, lastPointerCanceledFrame);

    private void BeginLocationPointerFrame()
    {
        pointerSelection = null;
        var io = Gui.GetIO();
        if (io.AppFocusLost || io.WantTextInput || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            || pointerOrigin != null && (!IsCurrentLocationPointer() || !Gui.IsMouseDown(ImGuiMouseButton.Left) && !Gui.IsMouseReleased(ImGuiMouseButton.Left)))
        { if (pointerOrigin != null) lastPointerCanceledFrame = Gui.GetFrameCount(); ClearLocationPointer(); return; }
        if (pointerOrigin != null && Gui.IsMouseDragging(ImGuiMouseButton.Left)) pointerDragged = true;
    }
    private bool IsCurrentLocationPointer()
        => pointerOrigin != null && pointerWorkspace != null && pointerWorkspace.TryGetTarget(out var owner)
            && ReferenceEquals(owner, host.Workspace) && pointerSelectionVersion == host.Browser.SelectionVersion
            && ReferenceEquals(host.Browser.FindLocation(pointerOrigin.Id), pointerOrigin);

    private void CaptureLocationPointer(AssetBrowserLocation location, bool editing = false)
    {
        var io = Gui.GetIO();
        if (editing || io.AppFocusLost || io.WantTextInput) return;
        if (Gui.IsItemClicked(ImGuiMouseButton.Left) && !Gui.IsItemToggledOpen() && host.Workspace is { } workspace)
        {
            lastPointerPressedId = location.Id; lastPointerPressedFrame = Gui.GetFrameCount();
            pointerOrigin = location; pointerWorkspace = new(workspace);
            pointerSelectionVersion = host.Browser.SelectionVersion; pointerDragged = false;
        }
        if (pointerOrigin == null || !Gui.IsMouseReleased(ImGuiMouseButton.Left) || !Gui.IsItemHovered()
            || pointerDragged || !IsCurrentLocationPointer()) return;
        pointerSelection = location; pointerToggle = io.KeyCtrl; pointerRange = io.KeyShift;
    }
    private void CompleteLocationPointerFrame()
    {
        if (pointerSelection is { } location && IsCurrentLocationPointer())
        {
            ApplyLocationSelection(location, pointerToggle, pointerRange);
            lastPointerAcceptedId = location.Id; lastPointerAcceptedFrame = Gui.GetFrameCount();
        }
        pointerSelection = null;
        if (!Gui.IsMouseDown(ImGuiMouseButton.Left)) ClearLocationPointer();
    }
    private void ClearLocationPointer()
    {
        pointerOrigin = pointerSelection = null; pointerWorkspace = null;
        pointerDragged = pointerToggle = pointerRange = false;
    }
}
