using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class CurveEditorPanel
{
    private sealed record CurveKeyboardRow(CurveBinding Binding, ICurveTrack Track, EditorTreeKeyboardGeometry Geometry,
        CurveHierarchyNode Node, CurveHierarchyNode? Parent, bool Expanded);
    private readonly List<CurveKeyboardRow> curveKeyboardRows = [];
    private uint curveFocusedRow;
    private ImGuiKey curveKeyboardKey;
    private bool curveKeyboardControl;
    private static readonly ImGuiKey[] CurveSelectionKeys = [ImGuiKey.UpArrow, ImGuiKey.DownArrow, ImGuiKey.Home,
        ImGuiKey.End, ImGuiKey.Space, ImGuiKey.Delete, ImGuiKey.LeftArrow, ImGuiKey.RightArrow];
    private void BeginCurveKeyboard()
    {
        curveKeyboardKey = ImGuiKey.None;
        var context = Gui.GetCurrentContext(); var io = Gui.GetIO();
        curveFocusedRow = context.NavId;
        var window = context.NavWindow.IsNull ? 0 : context.NavWindow.ID;
        if (Gui.IsWindowFocused() && !Gui.IsAnyItemActive() && !io.WantTextInput && !io.AppFocusLost && !io.KeyAlt && !io.KeySuper
            && !context.DragDropActive && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && curveKeyboardRows.Any(row => row.Geometry.Identity.State.RowId == curveFocusedRow
                && row.Geometry.Identity.State.WindowId == window && row.Geometry.Identity.State.Frame == Gui.GetFrameCount() - 1))
        {
            foreach (var key in CurveSelectionKeys) if (Gui.IsKeyPressed(key)) { curveKeyboardKey = key; break; }
            if (curveKeyboardKey != ImGuiKey.None)
            {
                curveKeyboardControl = io.KeyCtrl;
                EditorTreeKeyboardAdapter.TakeNavigationKey(curveFocusedRow, curveKeyboardKey == ImGuiKey.Space);
            }
        }
        curveKeyboardRows.Clear();
    }
    private void CompleteCurveKeyboard()
    {
        if (curveKeyboardKey == ImGuiKey.None) return;
        var index = curveKeyboardRows.FindIndex(row => row.Geometry.Identity.State.RowId == curveFocusedRow);
        if (index < 0) return;
        if (curveKeyboardKey == ImGuiKey.Delete) { Run(RemoveCurveFromView); return; }
        var focused = curveKeyboardRows[index];
        if (curveKeyboardKey is ImGuiKey.LeftArrow or ImGuiKey.RightArrow)
        {
            var expand = curveKeyboardKey == ImGuiKey.RightArrow;
            if (focused.Node.Children.Count != 0 && focused.Expanded != expand)
            {
                EditorTreeStateAdapter.TrySetExpanded(focused.Geometry.Identity.State, expand, out _);
                return;
            }
        }
        var targetIndex = curveKeyboardKey switch
        {
            ImGuiKey.UpArrow => index - 1, ImGuiKey.DownArrow => index + 1,
            ImGuiKey.Home => 0, ImGuiKey.End => curveKeyboardRows.Count - 1,
            ImGuiKey.LeftArrow => focused.Parent == null ? index : curveKeyboardRows.FindIndex(row => ReferenceEquals(row.Node, focused.Parent)),
            ImGuiKey.RightArrow => focused.Expanded && focused.Node.Children.Count != 0 ? index + 1 : index,
            _ => index
        };
        if (targetIndex < 0 || targetIndex >= curveKeyboardRows.Count) return;
        var target = curveKeyboardRows[targetIndex];
        if (!target.Binding.IsCurrent || !bindings.Contains(target.Binding) || removed.Contains(target.Track)) return;
        if (!TryValidatePending()) return;
        if (!EditorTreeKeyboardAdapter.Focus(target.Geometry)) return;
        if (!curveKeyboardControl || curveKeyboardKey is ImGuiKey.Space or ImGuiKey.Home or ImGuiKey.End)
            Run(() => { active = target.Track; ResetSelection(); Fit(0); });
    }
}
