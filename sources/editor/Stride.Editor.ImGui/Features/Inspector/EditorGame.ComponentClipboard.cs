using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string componentClipboardText = "";
    private Type? componentClipboardType;
    private EntityHierarchyDocument? componentClipboardDocument;
    private PendingComponentPaste? pendingComponentPaste;
    private sealed record PendingComponentPaste(EntityHierarchyDocument Document, EntityComponent Component, string Text);

    private void DrawComponentClipboardActions(EntityComponent component)
    {
        var source = document!;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, source.CanCopyComponent(component))) Execute(() =>
        {
            if (!ReferenceEquals(document, source) || !source.CanCopyComponent(component)) return;
            ValidatePendingPropertyStrings();
            if (!ReferenceEquals(document, source) || !source.CanCopyComponent(component)) return;
            Gui.SetClipboardText(source.CopyComponent(component));
        });
        var text = Gui.GetClipboardTextS() ?? "";
        if (text != componentClipboardText || !ReferenceEquals(componentClipboardDocument, document))
        {
            componentClipboardText = text;
            componentClipboardDocument = document;
            componentClipboardType = EntityHierarchyDocument.ComponentClipboardType(text);
        }
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, document!.CanPasteComponentValues(component, componentClipboardType)))
            pendingComponentPaste = new PendingComponentPaste(document, component, componentClipboardText);
    }

    private void ApplyPendingComponentPaste()
    {
        if (pendingComponentPaste == null) return;
        var request = pendingComponentPaste; pendingComponentPaste = null;
        if (!ReferenceEquals(document, request.Document)) return;
        var component = request.Component;
        if (activeColorPopup?.Component == component) activeColorPopup = null;
        Execute(() => { ValidatePendingPropertyStrings(); request.Document.PasteComponentValues(component, request.Text); });
        foreach (var field in componentErrors.Keys.Where(field => field.Component == component).ToArray()) componentErrors.Remove(field);
    }
}
