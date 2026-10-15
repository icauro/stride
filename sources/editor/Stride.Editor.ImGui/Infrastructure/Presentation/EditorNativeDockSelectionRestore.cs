using System.Globalization;
using System.Text.RegularExpressions;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>One-shot native selection intents belonging to one successfully loaded INI publication.</summary>
internal sealed class EditorNativeDockSelectionRestore
{
    private static readonly Regex Selection = new(@"(?m)^\s*Dock(?:Node|Space)\s+ID=0x([0-9A-Fa-f]+)[^\r\n]*\sSelected=0x([0-9A-Fa-f]+)(?:\s|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly Dictionary<uint, uint> pending = [];
    private readonly Dictionary<uint, int> requestedFrames = [];
    private readonly HashSet<uint> acknowledged = [];
    private int eligibleFrame = -1;
    private int lastQueueFrame = -1, lastAcknowledgedFrame = -1;
    private uint lastQueuedNode, lastQueuedTab, lastAcknowledgedNode, lastAcknowledgedTab;
    private readonly Dictionary<uint, uint> observedSelections = [];
    private readonly Dictionary<uint, (uint Selected, uint Visible, uint Next, uint Nav, int Popups)> observedStates = [];
    private readonly List<object> observations = [];
    private readonly List<object> expired = [];
    private readonly List<object> nonSelectable = [];
    private readonly Dictionary<uint, string> pendingReasons = [];
    internal bool HasPending => pending.Count != 0;
    internal unsafe object Inspection => new { eligibleFrame, lastQueueFrame, lastQueuedNode, lastQueuedTab,
        lastAcknowledgedFrame, lastAcknowledgedNode, lastAcknowledgedTab, observations, expired, nonSelectable,
        pending = pending.Select(item => new { node = item.Key, tab = item.Value,
            firstRequestedFrame = requestedFrames.GetValueOrDefault(item.Key, -1), acknowledged = acknowledged.Contains(item.Key),
            reason = pendingReasons.GetValueOrDefault(item.Key),
            readiness = DescribeReadiness(ImGuiP.DockBuilderGetNode(item.Key).Handle, item.Value, Gui.GetFrameCount()) }).ToArray() };

    internal void Capture(string nativeIni)
    {
        pending.Clear();
        requestedFrames.Clear(); acknowledged.Clear(); eligibleFrame = -1;
        lastQueueFrame = lastAcknowledgedFrame = -1;
        lastQueuedNode = lastQueuedTab = lastAcknowledgedNode = lastAcknowledgedTab = 0;
        observedSelections.Clear(); observedStates.Clear(); observations.Clear();
        expired.Clear(); nonSelectable.Clear(); pendingReasons.Clear();
        foreach (Match match in Selection.Matches(nativeIni))
            if (uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var node)
                && uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tab)
                && node != 0 && tab != 0) pending[node] = observedSelections[node] = tab;
    }

    internal unsafe void Observe(bool enabled, string boundary)
    {
        if (!enabled || observedSelections.Count == 0) return;
        var context = Gui.GetCurrentContext();
        var nav = context.NavWindow.IsNull ? 0 : context.NavWindow.ID;
        foreach (var intent in observedSelections)
        {
            var node = ImGuiP.DockBuilderGetNode(intent.Key).Handle;
            if (node == null || node->TabBar == null) continue;
            var bar = node->TabBar;
            var state = (bar->SelectedTabId, bar->VisibleTabId, bar->NextSelectedTabId, nav, context.OpenPopupStack.Size);
            if (observedStates.TryGetValue(intent.Key, out var previous) && previous == state) continue;
            observedStates[intent.Key] = state;
            if (observations.Count >= 256) observations.RemoveAt(0);
            observations.Add(new { boundary, frame = Gui.GetFrameCount(), node = intent.Key, expectedTab = intent.Value,
                selectedTab = bar->SelectedTabId, visibleTab = bar->VisibleTabId, nextTab = bar->NextSelectedTabId,
                navWindow = nav, navName = context.NavWindow.IsNull ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)context.NavWindow.Handle->Name),
                popupCount = context.OpenPopupStack.Size, activeId = context.ActiveId, pending = pending.ContainsKey(intent.Key),
                readiness = DescribeReadiness(node, intent.Value, Gui.GetFrameCount()) });
        }
    }

    private static unsafe object DescribeReadiness(ImGuiDockNode* node, uint expectedTab, int frame)
    {
        var bar = node == null ? null : node->TabBar;
        var target = bar == null ? default : ImGuiP.TabBarFindTabByID(new ImGuiTabBarPtr(bar), expectedTab);
        var visible = node == null ? null : node->VisibleWindow;
        var targetWindow = target.IsNull ? null : target.Handle->Window;
        return new { frame, exists = node != null, mergedFlags = node == null ? null : node->MergedFlags.ToString(),
            localFlags = node == null ? null : node->LocalFlags.ToString(),
            mergedFlagsRaw = node == null ? 0 : (int)node->MergedFlags, localFlagsRaw = node == null ? 0 : (int)node->LocalFlags,
            hiddenBar = node != null && (node->MergedFlags & (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.HiddenTabBar) != 0,
            noTabBar = node != null && (node->MergedFlags & (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.NoTabBar) != 0,
            windowCount = node == null ? 0 : node->Windows.Size, nodeSelected = node == null ? 0 : node->SelectedTabId,
            splitParent = node != null && (node->ChildNodes_0 != null || (&node->ChildNodes_0)[1] != null),
            nodeActiveFrame = node == null ? -1 : node->LastFrameActive,
            visibleWindowId = visible == null ? 0 : visible->ID, visibleTabId = visible == null ? 0 : visible->TabId,
            visibleDockId = visible == null ? 0 : visible->DockId,
            visibleActiveFrame = visible == null ? -1 : visible->LastFrameActive,
            visibleHidden = visible != null && visible->Hidden != 0,
            barExists = bar != null, barTabsCount = bar == null ? 0 : bar->Tabs.Size, targetExists = !target.IsNull,
            targetWindowId = targetWindow == null ? 0 : targetWindow->ID,
            targetDockId = targetWindow == null ? 0 : targetWindow->DockId,
            targetActiveFrame = targetWindow == null ? -1 : targetWindow->LastFrameActive,
            targetHidden = targetWindow != null && targetWindow->Hidden != 0 };
    }

    internal unsafe void PumpBeforeFrame()
    {
        if (pending.Count == 0) return;
        var context = Gui.GetCurrentContext();
        var frame = Gui.GetFrameCount();
        // Modal dismissal can return navigation to its previous tool window.
        // Publish the retained choice only once that modal lifecycle completes.
        if (context.OpenPopupStack.Size != 0) { eligibleFrame = -1; return; }
        if (eligibleFrame < 0) eligibleFrame = frame;
        foreach (var intent in pending.ToArray())
        {
            var node = ImGuiP.DockBuilderGetNode(intent.Key).Handle;
            // Missing/hidden panes must not retain an intent forever. Native
            // docking normally builds/submits its restored tabs within a few frames.
            if (frame - eligibleFrame > 120 || node == null && frame - eligibleFrame > 3)
            {
                expired.Add(new { frame, node = intent.Key, tab = intent.Value, reason = pendingReasons.GetValueOrDefault(intent.Key),
                    readiness = DescribeReadiness(node, intent.Value, frame) });
                pending.Remove(intent.Key); requestedFrames.Remove(intent.Key); acknowledged.Remove(intent.Key); pendingReasons.Remove(intent.Key); continue;
            }
            if (node == null) { pendingReasons[intent.Key] = "Native node not yet present"; continue; }
            var splitParent = node->ChildNodes_0 != null || (&node->ChildNodes_0)[1] != null;
            var dormantLeaf = !splitParent && node->Windows.Size == 0 && node->TabBar == null && node->VisibleWindow == null;
            if (splitParent || dormantLeaf && frame - eligibleFrame > 3)
            {
                // INI can retain Selected metadata on internal split nodes or
                // an inactive preview leaf. Neither has a selectable UI tab.
                // Keep that native metadata intact, but do not make canonical
                // document focus wait for a tab that is not currently submitted.
                nonSelectable.Add(new { frame, node = intent.Key, tab = intent.Value,
                    reason = splitParent ? "Internal split parent has no selectable tab" : "Dormant leaf has no submitted native window after construction grace",
                    readiness = DescribeReadiness(node, intent.Value, frame), uiAcknowledged = false });
                pending.Remove(intent.Key); requestedFrames.Remove(intent.Key); acknowledged.Remove(intent.Key); pendingReasons.Remove(intent.Key);
                continue;
            }
            if (dormantLeaf) { pendingReasons[intent.Key] = "Dormant leaf waiting bounded native construction grace"; continue; }
            var hiddenBar = (node->MergedFlags & (ImGuiDockNodeFlags)(ImGuiDockNodeFlagsPrivate.HiddenTabBar | ImGuiDockNodeFlagsPrivate.NoTabBar)) != 0;
            var visible = node->VisibleWindow;
            // Hidden singleton bars do not run native TabBarLayout, so their
            // NextSelectedTabId is never consumed. Certify the actual submitted
            // visible window instead of waiting on the inactive leaf/bar frame.
            if (hiddenBar && node->Windows.Size == 1 && visible != null && visible->TabId == intent.Value && node->SelectedTabId == intent.Value
                && visible->DockId == intent.Key && visible->LastFrameActive >= frame - 1 && visible->Hidden == 0)
            {
                acknowledged.Add(intent.Key);
                pendingReasons[intent.Key] = "Actual hidden singleton acknowledged; waiting for complete publication";
                lastAcknowledgedFrame = frame; lastAcknowledgedNode = intent.Key; lastAcknowledgedTab = intent.Value;
                continue;
            }
            if (node->TabBar == null) { acknowledged.Remove(intent.Key); pendingReasons[intent.Key] = "Native bar absent; singleton certificate unmet"; continue; }
            var bar = new ImGuiTabBarPtr(node->TabBar);
            var tab = ImGuiP.TabBarFindTabByID(bar, intent.Value);
            if (tab.IsNull) { acknowledged.Remove(intent.Key); pendingReasons[intent.Key] = "Saved native tab not in actual bar"; continue; }
            // A real different tab click supersedes the saved publication.
            if (frame > eligibleFrame && Gui.GetIO().MouseClicked[0] && context.HoveredId != 0 && context.HoveredId != intent.Value
                && !ImGuiP.TabBarFindTabByID(bar, context.HoveredId).IsNull)
            { pending.Remove(intent.Key); requestedFrames.Remove(intent.Key); acknowledged.Remove(intent.Key); continue; }
            if (requestedFrames.TryGetValue(intent.Key, out var requestedFrame) && frame > requestedFrame
                && bar.SelectedTabId == intent.Value && bar.VisibleTabId == intent.Value && tab.Handle->Window != null
                && tab.Handle->Window->DockId == intent.Key && tab.Handle->Window->LastFrameActive >= frame - 1 && tab.Handle->Window->Hidden == 0)
            {
                lastAcknowledgedFrame = frame; lastAcknowledgedNode = intent.Key; lastAcknowledgedTab = intent.Value;
                acknowledged.Add(intent.Key); pendingReasons[intent.Key] = "Actual visible native tab acknowledged; waiting for complete publication"; continue;
            }
            acknowledged.Remove(intent.Key);
            pendingReasons[intent.Key] = hiddenBar ? "Hidden bar singleton certificate unmet" : "Native selected/visible/current window certificate unmet";
            // Dear ImGui v1.92.2b DockNodeUpdateTabBar restores saved selection
            // on recreation, but selects the last-added window on later submissions.
            // https://github.com/ocornut/imgui/blob/v1.92.2b-docking/imgui.cpp
            // The native choice also calls FocusWindow for the selected tool.
            // Document focus restoration must wait until all choices settle.
            bar.NextSelectedTabId = intent.Value;
            lastQueueFrame = frame; lastQueuedNode = intent.Key; lastQueuedTab = intent.Value;
            requestedFrames.TryAdd(intent.Key, frame);
        }
        if (pending.Count == 0 || pending.Keys.Any(id => !acknowledged.Contains(id))) return;
        // Commit the complete publication together. Native docking first applies
        // the previous NavWindow's tab; retain these real requests for this last
        // layout pass, then the canonical document focus request can run after it.
        foreach (var intent in pending)
        {
            var node = ImGuiP.DockBuilderGetNode(intent.Key).Handle;
            if (node == null || node->TabBar == null
                || (node->MergedFlags & (ImGuiDockNodeFlags)(ImGuiDockNodeFlagsPrivate.HiddenTabBar | ImGuiDockNodeFlagsPrivate.NoTabBar)) != 0) continue;
            node->TabBar->NextSelectedTabId = intent.Value;
            lastQueueFrame = frame; lastQueuedNode = intent.Key; lastQueuedTab = intent.Value;
        }
        pending.Clear(); requestedFrames.Clear(); acknowledged.Clear();
    }
}
