using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class HierarchyPanel
{
    private readonly List<KeyboardRow> keyboardRows = [];
    private static readonly ImGuiKey[] NavigationKeys = [ImGuiKey.UpArrow, ImGuiKey.DownArrow, ImGuiKey.LeftArrow, ImGuiKey.RightArrow,
        ImGuiKey.Home, ImGuiKey.End, ImGuiKey.Space, ImGuiKey.KeypadAdd, ImGuiKey.KeypadSubtract];
    private object? keyboardTarget;
    private ImGuiKey keyboardKey;
    private uint keyboardFocusedId;
    private bool keyboardToggle, keyboardRange;
    private readonly record struct KeyboardRow(object? Target, EditorTreeKeyboardGeometry Geometry, ImGuiTreeNodeFlags Flags, bool Open);

    internal void SetKeyboardRowTarget(object target) => keyboardTarget = target;
    private void ClearKeyboardRows() { keyboardRows.Clear(); keyboardTarget = null; keyboardKey = ImGuiKey.None; }
    internal void RememberKeyboardRow(EditorTreeKeyboardIdentity identity, ImGuiTreeNodeFlags flags, bool open)
    {
        keyboardRows.Add(new(keyboardTarget, EditorTreeKeyboardAdapter.EndRow(identity), flags, open));
        keyboardTarget = null;
    }
    private void BeginKeyboardRows()
    {
        keyboardKey = ImGuiKey.None;
        keyboardFocusedId = Gui.GetCurrentContext().NavId;
        var navigationWindow = Gui.GetCurrentContext().NavWindow;
        var navigationWindowId = navigationWindow.IsNull ? 0 : navigationWindow.ID;
        var io = Gui.GetIO();
        if (Gui.IsWindowFocused() && !Gui.IsAnyItemActive() && !io.WantTextInput && !io.KeyAlt && !io.KeySuper
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            foreach (var key in NavigationKeys)
                if (Gui.IsKeyPressed(key)) { keyboardKey = key; break; }
            // A toolbar/filter focus must not become tree focus merely because an arrow was pressed.
            if (!keyboardRows.Exists(row => row.Geometry.Identity.State.RowId == keyboardFocusedId
                && row.Geometry.Identity.State.WindowId == navigationWindowId
                && row.Geometry.Identity.State.Frame == Gui.GetFrameCount() - 1)) keyboardKey = ImGuiKey.None;
            if (keyboardKey != ImGuiKey.None)
            {
                keyboardToggle = io.KeyCtrl; keyboardRange = io.KeyShift;
                EditorTreeKeyboardAdapter.TakeNavigationKey(keyboardFocusedId, keyboardKey == ImGuiKey.Space);
            }
        }
        keyboardRows.Clear(); keyboardTarget = null;
    }
    private void CompleteKeyboardRows()
    {
        if (keyboardKey == ImGuiKey.None) return;
        var focused = keyboardRows.FindIndex(row => row.Geometry.Identity.State.RowId == keyboardFocusedId);
        if (focused < 0) return;
        var row = keyboardRows[focused];
        var target = focused;
        var expandable = (row.Flags & ImGuiTreeNodeFlags.Leaf) == 0;
        bool? expand = null;
        switch (keyboardKey)
        {
            case ImGuiKey.UpArrow: target--; break;
            case ImGuiKey.DownArrow: target++; break;
            case ImGuiKey.Home: target = 0; break;
            case ImGuiKey.End: target = keyboardRows.Count - 1; break;
            case ImGuiKey.Space: break;
            case ImGuiKey.KeypadAdd: if (expandable) expand = true; break;
            case ImGuiKey.KeypadSubtract: if (expandable) expand = false; break;
            case ImGuiKey.RightArrow:
                if (!expandable) return;
                if (!row.Open) expand = true;
                else target++;
                break;
            case ImGuiKey.LeftArrow:
                if (expandable && row.Open) expand = false;
                else
                {
                    target--;
                    while (target >= 0 && keyboardRows[target].Geometry.Identity.Depth >= row.Geometry.Identity.Depth) target--;
                }
                break;
        }
        if (expand is { } expanded)
        {
            EditorTreeStateAdapter.TrySetExpanded(row.Geometry.Identity.State, expanded, out _);
            return;
        }
        if (keyboardKey is ImGuiKey.KeypadAdd or ImGuiKey.KeypadSubtract || target < 0 || target >= keyboardRows.Count) return;
        var chosen = keyboardRows[target];
        if (!EditorTreeKeyboardAdapter.Focus(chosen.Geometry)) return;
        var explicitSelection = keyboardKey is ImGuiKey.Space or ImGuiKey.Home or ImGuiKey.End;
        if ((!keyboardToggle || explicitSelection) && chosen.Target is { } actual)
            host.QueueHierarchyKeyboardSelection(actual, keyboardToggle && !keyboardRange, keyboardRange);
    }
}
