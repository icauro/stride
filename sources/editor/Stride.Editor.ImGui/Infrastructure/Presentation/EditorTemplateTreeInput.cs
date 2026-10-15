using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Current-row native navigation without retaining a context or window pointer.</summary>
internal static class EditorTemplateTreeInput
{
    internal static uint FocusedIdentity => Gui.GetCurrentContext().NavId;
    internal static void FocusCurrentRow(uint row)
    {
        var window = ImGuiP.GetCurrentWindow();
        ImGuiP.SetFocusID(row, window); ImGuiP.FocusWindow(window);
    }
    internal static void ClaimBoundaryKeys(uint row)
    {
        ImGuiP.SetKeyOwner(ImGuiKey.Home, row); ImGuiP.SetKeyOwner(ImGuiKey.End, row);
        ImGuiP.SetKeyOwner(ImGuiKey.PageUp, row); ImGuiP.SetKeyOwner(ImGuiKey.PageDown, row);
        ImGuiP.SetKeyOwner(ImGuiKey.KeypadAdd, row); ImGuiP.SetKeyOwner(ImGuiKey.KeypadSubtract, row);
        ImGuiP.SetKeyOwner(ImGuiKey.Enter, row); ImGuiP.SetKeyOwner(ImGuiKey.KeypadEnter, row);
        ImGuiP.SetKeyOwner(ImGuiKey.Escape, row);
    }
    internal static void ClaimListBoundaryKeys(uint row)
    {
        ImGuiP.SetKeyOwner(ImGuiKey.Home, row); ImGuiP.SetKeyOwner(ImGuiKey.End, row);
        ImGuiP.SetKeyOwner(ImGuiKey.PageUp, row); ImGuiP.SetKeyOwner(ImGuiKey.PageDown, row);
        ImGuiP.SetKeyOwner(ImGuiKey.Backspace, row);
    }
    internal static SelectionKeyScope SuppressSelectionKeyToggle(string id) => new(id);
    internal static SelectionKeyScope SuppressListTextSearchSpace(string id, bool searching) => searching ? new(id, true) : default;
    internal readonly ref struct SelectionKeyScope
    {
        private readonly bool suppressed;
        private readonly uint activation, down, pressed;
        internal SelectionKeyScope(string id, bool onlySearchSpace = false)
        {
            var context = Gui.GetCurrentContext();
            suppressed = context.NavId == Gui.GetID(id) && (Gui.IsKeyPressed(ImGuiKey.Space)
                || !onlySearchSpace && (Gui.IsKeyPressed(ImGuiKey.Enter) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter)));
            activation = context.NavActivateId; down = context.NavActivateDownId; pressed = context.NavActivatePressedId;
            if (suppressed) context.NavActivateId = context.NavActivateDownId = context.NavActivatePressedId = 0;
        }
        public void Dispose()
        {
            if (!suppressed) return;
            var context = Gui.GetCurrentContext();
            context.NavActivateId = activation; context.NavActivateDownId = down; context.NavActivatePressedId = pressed;
        }
    }
    internal static bool SelectsCurrentRow()
    {
        if (!Gui.IsItemFocused()) return false;
        var io = Gui.GetIO();
        if (io.WantTextInput || io.KeyAlt || io.KeySuper) return false;
        if (Gui.IsKeyPressed(ImGuiKey.Space))
        { ImGuiP.SetKeyOwner(ImGuiKey.Space, Gui.GetItemID()); return true; }
        // Native TreeView arrows with Control only move focus; Space then
        // toggles that focused group under the real Single selection policy.
        return !io.KeyCtrl && Gui.GetCurrentContext().NavJustMovedToId == Gui.GetItemID();
    }
}
