using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ref string sceneReferenceFilter => ref PropertyGrid.SceneReferenceFilter;

    private static string SceneReferenceLabel(object? value) => value switch
    {
        Entity entity => entity.Name,
        EntityComponent component => (component.Entity?.Name ?? component.GetType().Name) + " · " + component.GetType().Name,
        _ => "None",
    };

    private void DrawSceneReference(ComponentField field)
    {
        Gui.PushID(field.Name);
        if (!BeginDocumentedComponentRow(field)) { Gui.PopID(); return; }
        var value = field.Node.Retrieve();
        var display = value == null ? "" : value is EntityComponent referencedComponent ? referencedComponent.Entity?.Name ?? referencedComponent.GetType().Name : SceneReferenceLabel(value);
        var actionWidth = Gui.GetFrameHeight();
        Gui.SetNextItemWidth(Math.Max(actionWidth, Gui.GetContentRegionAvail().X - actionWidth * 3 - Gui.GetStyle().ItemSpacing.X * 3));
        Gui.InputText("##ReferenceName", ref display, 4096, ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
        if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/reference/text");
        if (document!.CanEditComponentValue(field) && field.SupportsEditing) DrawSceneReferenceDrop(field);
        Gui.SameLine(); Gui.SetNextItemWidth(actionWidth);
        Gui.BeginDisabled(!document.CanEditComponentValue(field) || !field.SupportsEditing);
        var open = Gui.BeginCombo("##SceneReference", "", ImGuiComboFlags.NoPreview);
        if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/reference");
        if (!open) DrawSceneReferenceDrop(field);
        if (open)
        {
            Gui.SetNextItemWidth(-1);
            Gui.InputTextWithHint("##Search", "Filter entities and components...", ref sceneReferenceFilter, 256);
            if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/reference/filter");
            void Candidate(object? candidate, string key)
            {
                var label = SceneReferenceLabel(candidate);
                if (candidate != null && !label.Contains(sceneReferenceFilter, StringComparison.OrdinalIgnoreCase)) return;
                Gui.PushID(key);
                var origin = Gui.GetCursorScreenPos();
                var selected = ReferenceEquals(value, candidate);
                var changed = Gui.Selectable("##Candidate", selected, ImGuiSelectableFlags.None,
                    new System.Numerics.Vector2(Math.Max(230, Gui.CalcTextSize(label).X + 12), 0));
                if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/reference/"
                    + (candidate is Entity referencedEntity ? referencedEntity.Id.ToString()
                        : candidate is EntityComponent referenced ? referenced.Id.ToString() : "none"));
                Gui.GetWindowDrawList().AddText(origin, Gui.GetColorU32(ImGuiCol.Text), label);
                if (Gui.IsItemHovered() && candidate != null)
                {
                    Gui.BeginTooltip(); Gui.TextUnformatted(key);
                    if (candidate is Entity entity && entity.Transform.Parent != null)
                        Gui.TextUnformatted("Parent: " + entity.Transform.Parent.Entity.Name);
                    Gui.EndTooltip();
                }
                if (selected) Gui.SetItemDefaultFocus();
                if (changed)
                {
                    CommitSceneReference(field, candidate);
                }
                Gui.PopID();
            }
            if (field.AllowsNullObject) Candidate(null, "none");
            foreach (var part in document.Asset.Hierarchy.Parts.Values.OrderBy(part => part.Entity.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (SceneObjectReference.AcceptsEntity(field.Type, part.Entity)) Candidate(part.Entity, part.Entity.Id.ToString());
                else foreach (var component in part.Entity.Components.Where(field.Type.IsInstanceOfType))
                    Candidate(component, part.Entity.Id + "/" + component.Id);
            }
            Gui.EndCombo();
        }
        Gui.SameLine(); Gui.BeginDisabled(value == null || !field.AllowsNullObject);
        if (Gui.Button("×##Clear", new System.Numerics.Vector2(actionWidth))) CommitSceneReference(field, null);
        if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/reference/clear");
        Gui.EndDisabled(); Gui.EndDisabled();
        Gui.SameLine(); Gui.BeginDisabled(value == null);
        if (icons!.Button("focus", "Select the referenced asset", 12) && value != null)
        {
                FinishEntityRename(true); document.EndEdits(); assetCommandContext = false;
                var entity = value is Entity referenced ? referenced : ((EntityComponent)value).Entity;
                if (document.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) && ReferenceEquals(part.Entity, entity))
                {
                    document.Selected = entity; revealCreatedParent = entity.Transform.Parent?.Entity.Id;
                    scrollHierarchyEntity = entity.Id; hierarchyFilter = "";
                }
        }
        Gui.EndDisabled();
        if (componentErrors.TryGetValue(field, out var message)) DrawWrappedText(message);
        Gui.EndTable(); Gui.PopID();
    }

    private void CommitSceneReference(ComponentField field, object? candidate)
    {
        try
        {
            assetCommandContext = false;
            document!.UpdateComponent(field, candidate); document.EndComponentEdit();
            componentErrors.Remove(field);
        }
        catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
    }

    private void DrawSceneReferenceDrop(ComponentField field)
        => DrawSceneReferenceDrop(field.Type, document!.CanEditComponentValue(field) && field.SupportsEditing, candidate => CommitSceneReference(field, candidate));

    private unsafe void DrawSceneReferenceDrop(Type targetType, bool canEdit, Action<object> assign)
    {
        if (!canEdit || document?.CanSave != true || !Gui.BeginDragDropTarget()) return;
        if (AcceptComponentReferenceDrop(targetType, assign)) { Gui.EndDragDropTarget(); return; }
        var payload = Gui.AcceptDragDropPayload(EntityDragType, ImGuiDragDropFlags.AcceptBeforeDelivery);
        if (!payload.IsNull && payload.DataSize == sizeof(EntityDragPayload))
        {
            var data = *(EntityDragPayload*)payload.Data;
            if (data.Document == document.DragIdentity && document.Asset.Hierarchy.Parts.TryGetValue(data.Entity, out var part))
            {
                object? candidate = null;
                var ambiguous = false;
                if (SceneObjectReference.AcceptsEntity(targetType, part.Entity)) candidate = part.Entity;
                else
                {
                    var matches = part.Entity.Components.Where(targetType.IsInstanceOfType).ToArray();
                    ambiguous = matches.Length > 1;
                    if (matches.Length == 1) candidate = matches[0];
                }
                Gui.BeginTooltip();
                Gui.TextUnformatted(candidate != null ? "Reference " + SceneReferenceLabel(candidate)
                    : ambiguous ? "Multiple compatible components: choose one in the picker" : "This entity has no compatible reference target");
                Gui.EndTooltip();
                if (candidate != null && payload.IsDelivery()) assign(candidate);
            }
        }
        Gui.EndDragDropTarget();
    }
}
