using Hexa.NET.ImGui;
using Stride.UI;
using Stride.UI.Panels;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private void DrawElement(UIDocument source, UIElement element)
    {
        Gui.PushID(element.Id.ToString());
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (selected.Contains(element.Id)) flags |= ImGuiTreeNodeFlags.Selected;
        if (element.VisualChildren.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;
        // UI part identity owns expansion/context/input state across actual Name edits.
        var label = UIElementTreeLabel(element);
        var forceSubmission = IsElementContextOpen(label);
        var sourceLibrary = source.SourceLibrary(element.Id);
        var libraryName = sourceLibrary == null ? null : host.LibraryName(sourceLibrary.BasePartAsset.Id);
        bool virtualized;
        var renaming = source.RenamingElement == element.Id;
        // The inline InputText is submitted over this tree row. Let its actual
        // input own the overlapping hit region instead of the row consuming it.
        if (renaming) flags |= ImGuiTreeNodeFlags.AllowOverlap;
        var keyboardIdentity = EditorTreeKeyboardAdapter.BeginRow(label);
        var open = renaming ? host.DrawRenameTreeNode(label, flags, out virtualized)
            : host.DrawElementTreeNode(label, element.Name ?? "", element.GetType().Name, libraryName,
                element.VisualChildren.Count, displayedRoot == element.Id, flags, forceSubmission, out virtualized);
        var rowMinimum = Gui.GetItemRectMin(); var rowMaximum = Gui.GetItemRectMax();
        if (!virtualized) host.RecordDiagnosticControl("ui/element/" + element.Id);
        RememberUITreeRow(source, element, keyboardIdentity, flags, open);
        hierarchyRowBounds[element.Id] = (rowMinimum, rowMaximum);
        if (!virtualized)
        {
            if (!renaming && Gui.IsItemClicked() && !Gui.IsItemToggledOpen()) QueueUITreePointerSelection(element.Id);
            host.RegisterCaptureItem("ui.element." + element.Id, "treeitem", element.Name ?? element.GetType().Name);
            if (renaming) DrawInlineElementRename(source, element, rowMinimum, rowMaximum);
            else DrawHierarchyDragSource(source, element);
            DrawHierarchyRowDrop(source, element, open, rowMinimum, rowMaximum);
            if (Gui.BeginPopupContextItem("Element"))
            {
                try
                {
                    if (!selected.Contains(element.Id)) Select(element.Id, false);
                    DrawElementCommands(source, element);
                }
                finally { Gui.EndPopup(); }
            }
        }
        if (open) { foreach (var child in element.VisualChildren.ToArray()) DrawElement(source, child); Gui.TreePop(); }
        Gui.PopID();
    }
    private static bool IsElementContextOpen(string label)
    {
        // A tree pushes its own row ID when open. Preserve both actual popup parent scopes.
        if (ImGuiP.IsPopupOpen(Gui.GetID("Element"), ImGuiPopupFlags.None)) return true;
        Gui.PushID(label);
        try { return ImGuiP.IsPopupOpen(Gui.GetID("Element"), ImGuiPopupFlags.None); }
        finally { Gui.PopID(); }
    }
}
