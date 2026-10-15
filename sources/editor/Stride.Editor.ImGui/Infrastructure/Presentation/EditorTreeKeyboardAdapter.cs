using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal readonly record struct EditorTreeKeyboardIdentity(EditorTreeRowState State, int Depth, uint FocusScope);
internal readonly record struct EditorTreeKeyboardGeometry(EditorTreeKeyboardIdentity Identity, Vector2 Minimum, Vector2 Maximum);

/// <summary>Only managed identity and geometry survive submission; each operation resolves its native window afresh.</summary>
internal static class EditorTreeKeyboardAdapter
{
    internal static unsafe EditorTreeKeyboardIdentity BeginRow(string label)
    {
        var window = ImGuiP.GetCurrentWindowRead();
        return new(EditorTreeStateAdapter.RememberCurrentRow(label), window.IsNull ? 0 : window.Handle->DC.TreeDepth,
            ImGuiP.GetCurrentFocusScope());
    }
    internal static EditorTreeKeyboardGeometry EndRow(EditorTreeKeyboardIdentity identity)
        => new(identity, Gui.GetItemRectMin(), Gui.GetItemRectMax());

    internal static bool Focus(EditorTreeKeyboardGeometry row)
    {
        var state = row.Identity.State;
        if (state.Frame != Gui.GetFrameCount()) return false;
        var window = ImGuiP.FindWindowByID(state.WindowId);
        if (window.IsNull || window.Hidden || window.LastFrameActive != state.Frame) return false;
        ImGuiP.FocusWindow(window);
        ImGuiP.SetFocusID(state.RowId, window);
        ImGuiP.SetNavID(state.RowId, ImGuiNavLayer.Main, row.Identity.FocusScope,
            new ImRect(row.Minimum - window.Pos, row.Maximum - window.Pos));
        Gui.SetNavCursorVisible(true);
        if (row.Minimum.Y < window.ClipRect.Min.Y)
            ImGuiP.SetScrollFromPosY(window, row.Minimum.Y - window.Pos.Y, 0);
        else if (row.Maximum.Y > window.ClipRect.Max.Y)
            ImGuiP.SetScrollFromPosY(window, row.Maximum.Y - window.Pos.Y, 1);
        return true;
    }

    internal static void TakeNavigationKey(uint focusedId, bool selectBySpace)
    {
        ImGuiP.NavMoveRequestCancel();
        if (!selectBySpace) return;
        var context = Gui.GetCurrentContext();
        // Space selects a WPF tree row; it does not activate the native expansion arrow.
        if (context.NavActivateId == focusedId) context.NavActivateId = 0;
        if (context.NavActivateDownId == focusedId) context.NavActivateDownId = 0;
        if (context.NavActivatePressedId == focusedId) context.NavActivatePressedId = 0;
    }
}
