namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    // Query native tab geometry without selecting/focusing windows directly.
    // Activation itself still travels through the same synthetic mouse input.
    private bool TryClickToolTab(string identity)
    {
        if (game.InspectDockTabCenter(identity) is not { } center) return false;
        var id = "tooltab/" + identity;
        game.Controls.Centers[id] = center;
        return Click(id);
    }

    private bool ClickInspectorControl(string id)
        => ClickToolControl(id, "editor.properties");

    private bool ClickPopupControl(string id)
    {
        if (game.Controls.Centers.ContainsKey(id)) return Click(id);
        lastMissingControl = id;
        if (game.InspectWorkflowPopupBody() is not { } center) return false;
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.AddMousePosEvent(center.X, center.Y);
        io.AddMouseWheelEvent(0, -1);
        RecordAction("scroll-popup", id);
        wait = 2;
        return false;
    }

    private bool ClickToolControl(string id, string tool)
    {
        if (game.Controls.Centers.ContainsKey(id)) return Click(id);
        if (game.InspectWorkflowToolBody(tool) is not { } body || !body.Selected)
        { RecordAction("tool-body-unavailable", tool); TryClickToolTab(tool); return false; }
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.AddMousePosEvent(body.Center.X, body.Center.Y);
        io.AddMouseWheelEvent(0, -1);
        RecordAction("scroll-tool", id, body.Center.X, body.Center.Y, io.MousePos.X, io.MousePos.Y);
        wait = 2;
        lastMissingControl = id;
        return false;
    }
}

internal sealed partial class EditorGame
{
    internal unsafe object? InspectWorkflowPropertyScroll()
    {
        var window = Hexa.NET.ImGui.ImGuiP.FindWindowByName(EditorPanels.PropertyGrid.DisplayWindowName);
        if (window.IsNull) return null;
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        return new { Frame = Hexa.NET.ImGui.ImGui.GetFrameCount(), window.LastFrameActive,
            ScrollY = window.Scroll.Y, ScrollMaxY = window.ScrollMax.Y,
            ClipMinX = window.InnerClipRect.Min.X, ClipMinY = window.InnerClipRect.Min.Y,
            ClipMaxX = window.InnerClipRect.Max.X, ClipMaxY = window.InnerClipRect.Max.Y,
            window.DockTabIsVisible, window.SkipItems,
            window.TabId, SelectedTab = window.DockNode.IsNull || window.DockNode.TabBar.IsNull ? 0u : window.DockNode.TabBar.SelectedTabId,
            Body = InspectWorkflowToolBody("editor.properties"),
            HoveredWindow = context.HoveredWindow.IsNull ? 0u : context.HoveredWindow.ID };
    }

    internal unsafe System.Numerics.Vector2? InspectWorkflowPopupBody()
    {
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        if (context.IsNull || context.OpenPopupStack.Size == 0) return null;
        var window = new Hexa.NET.ImGui.ImGuiWindowPtr(context.OpenPopupStack[context.OpenPopupStack.Size - 1].Window);
        if (window.IsNull || Hexa.NET.ImGui.ImGui.GetFrameCount() - window.LastFrameActive is not (0 or 1)) return null;
        var rect = window.InnerClipRect;
        if (rect.Max.X <= rect.Min.X || rect.Max.Y <= rect.Min.Y) return null;
        return (rect.Min + rect.Max) * .5f;
    }

    internal unsafe (System.Numerics.Vector2 Center, bool Selected)? InspectWorkflowToolBody(string identity)
    {
        var panel = EditorPanels.All.SingleOrDefault(panel => panel.CaptureId == identity);
        if (string.IsNullOrEmpty(panel.WindowName)) return null;
        var window = Hexa.NET.ImGui.ImGuiP.FindWindowByName(panel.DisplayWindowName);
        if (window.IsNull || Hexa.NET.ImGui.ImGui.GetFrameCount() - window.LastFrameActive is not (0 or 1)) return null;
        // ClipRect is the draw stack's transient clip, restored by End(). The
        // retained content clip remains the actual window body between frames.
        var rect = window.InnerClipRect;
        if (rect.Max.X <= rect.Min.X || rect.Max.Y <= rect.Min.Y) return null;
        var selected = !window.DockIsActive || window.DockNode.IsNull || window.DockNode.TabBar.IsNull
            || window.DockNode.TabBar.SelectedTabId == window.TabId;
        return ((rect.Min + rect.Max) * .5f, selected);
    }
}
