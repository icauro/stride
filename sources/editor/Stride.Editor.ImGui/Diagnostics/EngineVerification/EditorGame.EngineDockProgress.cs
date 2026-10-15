using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal unsafe object CaptureEngineDockProgress()
    {
        var tabs = new List<object>();
        foreach (var panel in EditorPanels.All)
        {
            var window = ImGuiP.FindWindowByName(panel.DisplayWindowName);
            if (window.IsNull || !window.DockIsActive || window.DockNode.IsNull || window.DockNode.TabBar.IsNull) continue;
            var bar = window.DockNode.TabBar;
            tabs.Add(new { panel.CaptureId, panel.DisplayWindowName, windowTab = window.TabId,
                selectedTab = bar.SelectedTabId, visibleTab = bar.VisibleTabId, frame = window.LastFrameActive,
                currentFrame = Gui.GetFrameCount() });
        }
        return tabs;
    }
}
