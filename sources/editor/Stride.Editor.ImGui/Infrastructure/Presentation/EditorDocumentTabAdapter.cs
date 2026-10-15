using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Resolves native tab layout/reorder state only within the current frame.</summary>
internal sealed class EditorDocumentTabAdapter
{
    private Guid currentDocumentId;
    private uint currentWindowId, currentBarId, currentTabId;
    private int currentFrame = -1;
    private readonly Dictionary<Guid, uint> documentTabs = [];
    private readonly Dictionary<Guid, object> nativeTabGeometry = [];
    private Guid[]? requestedOrder;
    internal Guid? RequestedSelection { get; private set; }
    internal Guid? RequestedFocus { get; private set; }
    private uint selectedNativeTab, visibleNativeTab;
    private int lastFocusAckFrame = -1, observedFocusPopupCount;
    private uint lastFocusAckNavWindowId;
    private Guid[] nativeOrder = [];
    internal object Inspection => new { currentFrame, currentWindowId, currentBarId, selectedNativeTab, visibleNativeTab, nativeOrder, requestedOrder, RequestedSelection, RequestedFocus,
        lastFocusAckFrame, lastFocusAckNavWindowId, observedFocusPopupCount,
        documentTabs = documentTabs.Select(pair => new { document = pair.Key, nativeTab = pair.Value }).ToArray(),
        nativeTabGeometry = nativeTabGeometry.Select(pair => new { document = pair.Key, geometry = pair.Value }).ToArray() };

    internal void RequestSelection(Guid documentId, bool requestFocus = true)
    { RequestedSelection = documentId; RequestedFocus = requestFocus ? documentId : null; }
    internal void CancelSelectionRequest() { RequestedSelection = null; RequestedFocus = null; }
    internal void CompleteFocus(Guid documentId, bool focused)
    {
        var context = Gui.GetCurrentContext();
        observedFocusPopupCount = context.OpenPopupStack.Size;
        // Popup close restores its prior navigation window. A temporary focus
        // underneath a live modal is not acknowledgment of the requested pane.
        if (!focused || RequestedFocus != documentId || RequestedSelection != null || observedFocusPopupCount != 0) return;
        RequestedFocus = null;
        lastFocusAckFrame = Gui.GetFrameCount();
        lastFocusAckNavWindowId = context.NavWindow.IsNull ? 0 : context.NavWindow.ID;
    }

    internal void RequestOrder(IReadOnlyList<EditorDocumentEntry> entries)
        => requestedOrder = entries.Select(entry => entry.Id).ToArray();

    internal bool SelectionSettled(Guid documentId)
    {
        var bar = ImGuiP.GetCurrentTabBar();
        selectedNativeTab = bar.IsNull ? 0 : bar.SelectedTabId;
        visibleNativeTab = bar.IsNull ? 0 : bar.VisibleTabId;
        var settled = !bar.IsNull && documentTabs.TryGetValue(documentId, out var id)
            && bar.SelectedTabId == id && bar.VisibleTabId == id;
        if (RequestedSelection is { } requested && documentTabs.TryGetValue(requested, out var requestedId) && !bar.IsNull)
        {
            if (requested == documentId && settled) RequestedSelection = null;
            else bar.NextSelectedTabId = requestedId;
        }
        return settled;
    }

    internal bool CompleteOrder(IReadOnlyList<EditorDocumentEntry> entries)
    {
        if (requestedOrder == null) return true;
        var desired = entries.Select(entry => entry.Id).ToArray();
        if (!requestedOrder.SequenceEqual(desired)) requestedOrder = desired;
        var bar = ImGuiP.GetCurrentTabBar();
        if (bar.IsNull) return false;
        var positions = new List<(Guid Document, int Index)>();
        foreach (var document in requestedOrder)
        {
            if (!documentTabs.TryGetValue(document, out var id)) return false;
            var tab = ImGuiP.TabBarFindTabByID(bar, id);
            if (tab.IsNull || tab.IndexDuringLayout < 0) return false;
            positions.Add((document, tab.IndexDuringLayout));
        }
        // Newly submitted tabs can retain an uninitialized layout index until
        // the next native layout. Never import those old indices as model order.
        if (positions.Select(item => item.Index).Distinct().Count() != positions.Count) return false;
        nativeOrder = positions.OrderBy(item => item.Index).Select(item => item.Document).ToArray();
        if (positions.OrderBy(item => item.Index).Select(item => item.Document).SequenceEqual(requestedOrder))
        { requestedOrder = null; return true; }
        for (var index = 0; index < requestedOrder.Length; index++)
        {
            var position = positions.First(item => item.Document == requestedOrder[index]).Index;
            if (position == index) continue;
            var tab = ImGuiP.TabBarFindTabByID(bar, documentTabs[requestedOrder[index]]);
            ImGuiP.TabBarQueueReorder(bar, tab, Math.Sign(index - position));
            return false; // Native reorder is applied at the next tab-bar layout.
        }
        return false;
    }

    public bool BeginItem(Guid documentId, string title, bool dirty, bool requestSelected, ref bool open, out int order, bool nativeText = false, bool recordDiagnostics = false)
    {
        var label = (nativeText ? title : "") + "###document/" + documentId;
        var tabId = Gui.GetID(label);
        var flags = dirty ? ImGuiTabItemFlags.UnsavedDocument : ImGuiTabItemFlags.None;
        if (requestSelected || RequestedSelection == documentId) flags |= ImGuiTabItemFlags.SetSelected;
        if (!nativeText) Gui.SetNextItemWidth(Gui.CalcTextSize(title).X + Gui.GetStyle().FramePadding.X * 2 + Gui.GetFrameHeight() * 2);
        var selected = Gui.BeginTabItem(label, ref open, flags);
        var bar = ImGuiP.GetCurrentTabBar();
        var window = ImGuiP.GetCurrentWindowRead();
        currentDocumentId = documentId;
        currentTabId = tabId;
        documentTabs[documentId] = tabId;
        currentWindowId = window.IsNull ? 0 : window.ID;
        currentBarId = bar.IsNull ? 0 : bar.ID;
        currentFrame = Gui.GetFrameCount();
        order = -1;
        if (!bar.IsNull)
        {
            var tab = ImGuiP.TabBarFindTabByID(bar, tabId);
            if (!tab.IsNull)
            {
                order = tab.IndexDuringLayout;
                if (!recordDiagnostics) return selected;
                var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
                nativeTabGeometry[documentId] = new { frame = currentFrame, tabId, selected,
                    itemId = Gui.GetItemID(), itemHovered = Gui.IsItemHovered(), itemClicked = Gui.IsItemClicked(ImGuiMouseButton.Left),
                    minX = minimum.X, minY = minimum.Y, maxX = maximum.X, maxY = maximum.Y,
                    tab.Offset, tab.Width, tab.IndexDuringLayout,
                    barX = bar.BarRect.Min.X, barMaxX = bar.BarRect.Max.X,
                    bar.ScrollingAnim, bar.ScrollingRectMinX, bar.ScrollingRectMaxX,
                    windowClipMinX = window.ClipRect.Min.X, windowClipMaxX = window.ClipRect.Max.X };
            }
        }
        return selected;
    }

    public bool QueueReorder(Guid documentId, int direction)
    {
        if (direction is not (-1 or 1) || documentId != currentDocumentId || currentFrame != Gui.GetFrameCount()) return false;
        // Context-menu actions run in the popup window, so resolve the owner by
        // its managed ID instead of confusing the popup with the document pane.
        var window = ImGuiP.FindWindowByID(currentWindowId);
        var bar = ImGuiP.GetCurrentTabBar();
        if (window.IsNull || window.LastFrameActive != currentFrame || bar.IsNull || bar.ID != currentBarId) return false;
        var tab = ImGuiP.TabBarFindTabByID(bar, currentTabId);
        if (tab.IsNull) return false;
        requestedOrder = null; // An explicit user's reorder supersedes an earlier transfer intent.
        ImGuiP.TabBarQueueReorder(bar, tab, direction);
        return true;
    }
}
