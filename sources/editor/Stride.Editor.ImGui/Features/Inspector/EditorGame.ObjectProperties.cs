using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private PendingObjectProperty? pendingObjectProperty;
    private sealed record PendingObjectProperty(EntityHierarchyDocument Document, ComponentField Field, AbstractNodeEntry Entry);

    private void DrawObjectProperty(ComponentField field)
    {
        var value = field.Node.Retrieve();
        Gui.PushID(field.Name);
        var open = false;
        if (field.Children.Count != 0)
        {
            ApplyPropertyExpansion(field);
            open = Gui.TreeNodeEx("##Object", ImGuiTreeNodeFlags.None);
            if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/expand");
            if (Controls.Enabled) Controls.Expanded["component/" + field.Component.Id + "/" + field.Name + "/expand"] = open;
            Gui.SameLine();
        }
        var inline = field.Presentation?.InlineMember(field);
        if (inline == null) DrawDocumentedComponentLabel(field);
        else if (!DrawInheritedComponentField(inline, field.Label)) DrawComponentFieldValue(inline with { Label = field.Label });
        DrawInlineEnabled(field.Children);
        if (field.SupportsObjectLifecycle)
        {
            Gui.SetNextItemWidth(-1);
            Gui.BeginDisabled(!document!.CanChangeObjectProperty(field));
            var entries = document.ObjectEntries(field);
            if (entries.Count != 0 && Gui.BeginCombo("##Type", NativeAbstractEntryControl.Label(entries, value)))
            {
                NativeAbstractEntryControl.Choices(entries, value, entry => pendingObjectProperty = new(document, field, entry));
                Gui.EndCombo();
            }
            Gui.EndDisabled();
        }
        if (open)
        {
            DrawComponentFields(field.Children);
            Gui.TreePop();
        }
        Gui.PopID();
    }

    private void ApplyPendingObjectProperty()
    {
        if (pendingObjectProperty == null) return;
        var request = pendingObjectProperty; pendingObjectProperty = null;
        if (!ReferenceEquals(document, request.Document)) return;
        if (activeColorPopup?.Component == request.Field.Component) activeColorPopup = null;
        SelectComponentObjectEntry(request.Document, request.Field, request.Entry);
        foreach (var field in componentErrors.Keys.Where(field => field.Component == request.Field.Component).ToArray()) componentErrors.Remove(field);
    }
}
