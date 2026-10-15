using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<string, (int Frame, Vector2 Center)> inspectedDockTabs = new(StringComparer.Ordinal);

    // Call after all panels have been submitted, on the owning ImGui thread.
    // This records layout only. Tests activate tabs by mouse input, never by
    // FocusWindow, SetSelected, or a capture-target action.
    internal unsafe void RecordDockTabInspection()
    {
        foreach (var identity in inspectedDockTabs.Keys.ToArray()) Controls.Centers.Remove("tooltab/" + identity);
        inspectedDockTabs.Clear();
        if (!Controls.Enabled) return;
        var frame = Gui.GetFrameCount();
        foreach (var panel in EditorPanels.All)
        {
            var window = ImGuiP.FindWindowByName(panel.DisplayWindowName);
            if (window.IsNull || window.LastFrameActive != frame || !window.DockIsActive || window.DockNode.IsNull) continue;
            var bar = window.DockNode.TabBar;
            if (bar.IsNull || bar.CurrFrameVisible != frame || bar.WantLayout) continue;
            var tab = ImGuiP.TabBarFindTabByID(bar, window.TabId);
            if (tab.IsNull || tab.LastFrameVisible != frame || tab.Width <= 0) continue;

            // DockNodeUpdateTabBar records the actual TabItem bounding box in
            // each docked window, including inactive tabs. No title-width or
            // tab-offset arithmetic is needed.
            var rect = window.DC.DockTabItemRect;
            var minimum = Vector2.Max(rect.Min, bar.BarRect.Min);
            var maximum = Vector2.Min(rect.Max, bar.BarRect.Max);
            if ((tab.Flags & (ImGuiTabItemFlags.Leading | ImGuiTabItemFlags.Trailing)) == 0)
            {
                minimum.X = Math.Max(minimum.X, bar.ScrollingRectMinX);
                maximum.X = Math.Min(maximum.X, bar.ScrollingRectMaxX);
            }
            if (!float.IsFinite(minimum.X) || !float.IsFinite(minimum.Y)
                || !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y)
                || maximum.Y <= minimum.Y || maximum.X - minimum.X < Gui.GetFrameHeight() * 2) continue;
            var center = (minimum + maximum) * .5f;
            inspectedDockTabs[panel.CaptureId] = (frame, center);
            Controls.Centers["tooltab/" + panel.CaptureId] = center;
        }
    }

    // Update runs before the next DrawPanels; accept its immediately previous
    // layout, but never leave a stale coordinate available across missing draws.
    internal Vector2? InspectDockTabCenter(string identity)
    {
        if (!Controls.Enabled || !inspectedDockTabs.TryGetValue(identity, out var state)) return null;
        var age = Gui.GetFrameCount() - state.Frame;
        return age is 0 or 1 ? state.Center : null;
    }
}
