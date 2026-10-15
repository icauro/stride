using Hexa.NET.ImGui;
using Stride.UI;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private readonly List<UIKeyboardRow> uiKeyboardRows = [];
    private UIDocument? uiKeyboardOwner;
    private Guid? uiTreeAnchor, uiTreePointerTarget;
    private bool uiTreeControl, uiTreeShift;
    private uint uiTreeFocusedId;
    private ImGuiKey uiTreeKey;
    private static readonly ImGuiKey[] UiTreeKeys = [ImGuiKey.UpArrow, ImGuiKey.DownArrow, ImGuiKey.LeftArrow,
        ImGuiKey.RightArrow, ImGuiKey.Home, ImGuiKey.End, ImGuiKey.Space, ImGuiKey.KeypadAdd, ImGuiKey.KeypadSubtract];
    private sealed record UIKeyboardRow(UIElement Element, UIElement? Parent, int Index,
        EditorTreeKeyboardGeometry Geometry, ImGuiTreeNodeFlags Flags, bool Open);

    private void BeginUITreeKeyboard(UIDocument source)
    {
        if (!ReferenceEquals(uiKeyboardOwner, source)) { uiKeyboardRows.Clear(); uiTreeAnchor = null; }
        uiKeyboardOwner = source; uiTreePointerTarget = null; uiTreeKey = ImGuiKey.None;
        var context = Gui.GetCurrentContext(); var io = Gui.GetIO();
        uiTreeFocusedId = context.NavId;
        var window = context.NavWindow.IsNull ? 0 : context.NavWindow.ID;
        if (Gui.IsWindowFocused() && !Gui.IsAnyItemActive() && !io.WantTextInput && !io.AppFocusLost && !io.KeyAlt && !io.KeySuper
            && !context.DragDropActive && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
            && uiKeyboardRows.Any(row => row.Geometry.Identity.State.RowId == uiTreeFocusedId
                && row.Geometry.Identity.State.WindowId == window && row.Geometry.Identity.State.Frame == Gui.GetFrameCount() - 1))
        {
            foreach (var key in UiTreeKeys) if (Gui.IsKeyPressed(key)) { uiTreeKey = key; break; }
            if (uiTreeKey != ImGuiKey.None)
            {
                uiTreeControl = io.KeyCtrl; uiTreeShift = io.KeyShift;
                EditorTreeKeyboardAdapter.TakeNavigationKey(uiTreeFocusedId, uiTreeKey == ImGuiKey.Space);
            }
        }
        uiKeyboardRows.Clear();
    }
    private void RememberUITreeRow(UIDocument source, UIElement element, EditorTreeKeyboardIdentity identity, ImGuiTreeNodeFlags flags, bool open)
    {
        IEnumerable<UIElement> siblings = element.VisualParent is { } parent ? parent.VisualChildren : source.Roots;
        uiKeyboardRows.Add(new(element, element.VisualParent, siblings.ToList().FindIndex(child => ReferenceEquals(child, element)),
            EditorTreeKeyboardAdapter.EndRow(identity), flags, open));
    }
    private void QueueUITreePointerSelection(Guid id)
    { uiTreePointerTarget = id; uiTreeControl = Gui.GetIO().KeyCtrl; uiTreeShift = Gui.GetIO().KeyShift; }

    private void CompleteUITreeKeyboard(UIDocument source)
    {
        if (uiTreePointerTarget is { } clicked)
        {
            var clickedIndex = uiKeyboardRows.FindIndex(row => row.Element.Id == clicked);
            if (clickedIndex >= 0) QueueUITreeSelection(source, clickedIndex, uiTreeControl, uiTreeShift);
            return;
        }
        if (uiTreeKey == ImGuiKey.None) return;
        var index = uiKeyboardRows.FindIndex(row => row.Geometry.Identity.State.RowId == uiTreeFocusedId);
        if (index < 0) return;
        var row = uiKeyboardRows[index]; var target = index;
        var expandable = (row.Flags & ImGuiTreeNodeFlags.Leaf) == 0;
        bool? expand = null;
        switch (uiTreeKey)
        {
            case ImGuiKey.UpArrow: target--; break;
            case ImGuiKey.DownArrow: target++; break;
            case ImGuiKey.Home: target = 0; break;
            case ImGuiKey.End: target = uiKeyboardRows.Count - 1; break;
            case ImGuiKey.Space: break;
            case ImGuiKey.KeypadAdd: if (expandable) expand = true; break;
            case ImGuiKey.KeypadSubtract: if (expandable) expand = false; break;
            case ImGuiKey.RightArrow:
                if (!expandable) return;
                if (!row.Open) expand = true; else target++;
                break;
            case ImGuiKey.LeftArrow:
                if (expandable && row.Open) expand = false;
                else
                {
                    target--;
                    while (target >= 0 && uiKeyboardRows[target].Geometry.Identity.Depth >= row.Geometry.Identity.Depth) target--;
                }
                break;
        }
        if (expand is { } expanded)
        { EditorTreeStateAdapter.TrySetExpanded(row.Geometry.Identity.State, expanded, out _); return; }
        if (uiTreeKey is ImGuiKey.KeypadAdd or ImGuiKey.KeypadSubtract || target < 0 || target >= uiKeyboardRows.Count) return;
        if (!EditorTreeKeyboardAdapter.Focus(uiKeyboardRows[target].Geometry)) return;
        if (!uiTreeControl || uiTreeKey is ImGuiKey.Space or ImGuiKey.Home or ImGuiKey.End)
            QueueUITreeSelection(source, target, uiTreeControl, uiTreeShift);
    }
    private void QueueUITreeSelection(UIDocument source, int index, bool control, bool shift)
    {
        var chosen = uiKeyboardRows[index];
        var anchor = uiTreeAnchor is { } id && selected.Contains(id) ? id : selected.FirstOrDefault();
        var first = shift ? uiKeyboardRows.FindIndex(row => row.Element.Id == anchor) : -1;
        UIKeyboardRow[] targets = first >= 0 ? uiKeyboardRows.Skip(Math.Min(first, index)).Take(Math.Abs(index - first) + 1).ToArray() : [chosen];
        var before = selected.ToArray(); var expectedAnchor = uiTreeAnchor;
        QueueUICommand(source, targets.Select(row => row.Element.Id).ToArray(), false, (actual, _) =>
        {
            if (!before.SequenceEqual(selected) || uiTreeAnchor != expectedAnchor) return;
            foreach (var target in targets)
            {
                var element = actual.Find(target.Element.Id);
                if (element == null || !ReferenceEquals(element, target.Element) || !ReferenceEquals(element.VisualParent, target.Parent)) return;
                IEnumerable<UIElement> siblings = element.VisualParent is { } parent ? parent.VisualChildren : actual.Roots;
                if (siblings.ToList().FindIndex(child => ReferenceEquals(child, element)) != target.Index) return;
            }
            if (shift && first >= 0)
            {
                selected.Clear(); selected.UnionWith(targets.Select(row => row.Element.Id));
                uiTreeAnchor = anchor;
            }
            else
            {
                if (!control) selected.Clear();
                if (control && selected.Contains(chosen.Element.Id)) selected.Remove(chosen.Element.Id); else selected.Add(chosen.Element.Id);
                uiTreeAnchor = selected.Contains(chosen.Element.Id) ? chosen.Element.Id : null;
            }
            RefreshSelection();
        });
    }
    private void ClearUITreeKeyboard()
    { uiKeyboardRows.Clear(); uiKeyboardOwner = null; uiTreeAnchor = uiTreePointerTarget = null; uiTreeKey = ImGuiKey.None; }
}
