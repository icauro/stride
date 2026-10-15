using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Native Single ListBoxItem input for material choices and template rows.</summary>
internal static class EditorIconStripInput
{
    internal static bool IsCaptureActive(uint identity)
        => identity != 0 && ImGuiP.GetActiveID() == identity && Gui.IsMouseDown(ImGuiMouseButton.Left);
    internal static bool IsPointerDragTarget(uint identity)
        => IsCaptureActive(identity) && Gui.GetIO().MouseDelta != Vector2.Zero
            && Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
    internal static void Focus(string id)
    {
        var identity = Gui.GetID(id); var window = ImGuiP.GetCurrentWindow();
        ImGuiP.SetFocusID(identity, window); ImGuiP.FocusWindow(window);
    }
    internal static void ClaimSearchKeys(string id)
    {
        var identity = Gui.GetID(id);
        ImGuiP.SetKeyOwner(ImGuiKey.Home, identity); ImGuiP.SetKeyOwner(ImGuiKey.End, identity);
        ImGuiP.SetKeyOwner(ImGuiKey.Backspace, identity);
    }
    internal static bool Draw(string id, bool selected, out bool deselect)
        => Draw(id, selected, out deselect, new Vector2(25, 24) * Gui.GetStyle().FontScaleDpi);
    internal static bool Draw(string id, bool selected, out bool deselect, Vector2 size,
        ImGuiSelectableFlags additionalFlags = ImGuiSelectableFlags.None)
    {
        var io = Gui.GetIO();
        var flags = ImGuiSelectableFlagsPrivate.SelectOnClick | ImGuiSelectableFlagsPrivate.NoPadWithHalfSpacing;
        if (!io.KeyCtrl) flags |= ImGuiSelectableFlagsPrivate.SelectOnNav;
        var activated = Gui.Selectable(id, selected,
            (ImGuiSelectableFlags)flags | additionalFlags,
            size);
        var left = Gui.IsItemClicked(ImGuiMouseButton.Left);
        var right = Gui.IsItemHovered() && ImGuiP.IsMouseClicked(ImGuiMouseButton.Right, ImGuiInputFlags.None, Gui.GetID(id));
        if (right)
        {
            var window = ImGuiP.GetCurrentWindow(); var identity = Gui.GetID(id);
            ImGuiP.SetFocusID(identity, window); ImGuiP.FocusWindow(window);
            ImGuiP.SetKeyOwner(ImGuiKey.MouseRight, identity);
            activated = true;
        }
        // Native ListBox AcceptsReturn is false here; Space is its selection
        // key, whereas Enter does not invoke a new primitive selection.
        if (!left && !right && (Gui.IsKeyPressed(ImGuiKey.Enter) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter))) activated = false;
        // Original ListBox Single selection permits Ctrl-click clearing. The
        // caller decides whether its actual bound selection can be null.
        deselect = activated && selected && io.KeyCtrl && (left || right || Gui.IsKeyPressed(ImGuiKey.Space));
        return activated;
    }
}
