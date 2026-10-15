using Hexa.NET.ImGui;
using Stride.Core.Serialization;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Core;
using NVector4 = System.Numerics.Vector4;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Dictionary<ComponentField, string> componentErrors => PropertyGrid.ComponentErrors;
    private void DrawComponentInspector(Entity entity)
    {
        if (activeColorPopup != null && activeColorPopup.Component.Entity != entity)
        {
            document!.EndComponentEdit(); activeColorPopup = null;
        }
        foreach (var component in entity.Components.Where(component => component is not TransformComponent))
        {
            if (component is Stride.Core.Yaml.IUnloadable unloaded)
            {
                Gui.PushID(component.Id.ToString());
                try { if (Gui.CollapsingHeader(unloaded.TypeName, ImGuiTreeNodeFlags.DefaultOpen)) DrawUnloadableProperty(unloaded); }
                finally { Gui.PopID(); }
                continue;
            }
            var fields = document!.GetComponentFields(component);
            Gui.PushID(component.Id.ToString());
            var enabledOverride = fields.FirstOrDefault(member => member.Node.Name == "Enabled" && !member.IsVisible)?.Node
                is Stride.Core.Assets.Quantum.IAssetMemberNode { BaseNode: not null } enabledNode && enabledNode.IsContentOverridden();
            var boldHeader = PushPackagePropertiesBoldFont(enabledOverride);
            var componentLabel = DisplayAttribute.GetDisplayName(component.GetType());
            if (string.IsNullOrEmpty(componentLabel) || componentLabel == component.GetType().Name)
                componentLabel = Stride.Core.Presentation.Core.Utils.SplitCamelCase(component.GetType().Name);
            var open = Gui.CollapsingHeader("##ComponentHeader", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);
            var headerMin = Gui.GetItemRectMin(); var headerMax = Gui.GetItemRectMax();
            if (boldHeader) Gui.PopFont();
            DrawComponentReferenceDragSource(component);
            DrawRemoveComponentAction(component);
            if (Controls.Enabled) Controls.Record("component/" + component.Id + "/header");
            if (Controls.Enabled) Controls.Expanded["component/" + component.Id + "/header"] = open;
            var afterHeader = Gui.GetCursorScreenPos();
            var enabledPosition = new System.Numerics.Vector2(headerMin.X + Gui.GetTreeNodeToLabelSpacing(),
                headerMin.Y + Math.Max(0, (headerMax.Y - headerMin.Y - Gui.GetFrameHeight()) * 0.5f));
            var hasEnabled = fields.Any(member => member.Node.Name == "Enabled" && member.Type == typeof(bool) && !member.IsVisible);
            DrawInlineEnabled(fields, enabledPosition);
            var removePosition = new System.Numerics.Vector2(headerMax.X - Gui.GetFrameHeight() - Gui.GetStyle().FramePadding.X,
                headerMin.Y + Math.Max(0, (headerMax.Y - headerMin.Y - Gui.GetFrameHeight()) * 0.5f));
            var labelPosition = new System.Numerics.Vector2(enabledPosition.X + (hasEnabled ? Gui.GetFrameHeight() + Gui.GetStyle().ItemInnerSpacing.X : 0),
                headerMin.Y + Math.Max(0, (headerMax.Y - headerMin.Y - Gui.GetTextLineHeight()) * 0.5f));
            var draw = Gui.GetWindowDrawList();
            draw.PushClipRect(labelPosition, new System.Numerics.Vector2(removePosition.X, headerMax.Y), true);
            draw.AddText(labelPosition, Gui.GetColorU32(ImGuiCol.Text), componentLabel); draw.PopClipRect();
            Gui.SetCursorScreenPos(removePosition); Gui.BeginDisabled(!document.CanRemoveComponent(component));
            Gui.PushStyleColor(ImGuiCol.Text, new NVector4(1, 0.35f, 0.2f, 1));
            if (Gui.Button("×##RemoveComponent", new System.Numerics.Vector2(Gui.GetFrameHeight()))) pendingComponentRemoval = component;
            Gui.PopStyleColor(); Gui.EndDisabled();
            Gui.SetCursorScreenPos(afterHeader);
            if (open)
            {
                if (fields.Count == 0) DrawFieldlessComponentVirtualRows(document!, component);
                else DrawComponentFields(fields);
            }
            else document!.EndComponentEdit(component);
            Gui.PopID();
        }
        ApplyPendingComponentRemoval();
        ApplyPendingObjectProperty();
        ApplyPendingCollectionAction();
    }
    private void DrawComponentField(ComponentField field)
    {
        if (!field.IsVisible) return;
        if (field.Node.Retrieve() is Stride.Core.Yaml.IUnloadable unloaded)
        {
            Gui.PushID(field.Name);
            try
            {
                DrawDocumentedComponentLabel(field);
                if (field.SupportsObjectLifecycle)
                {
                    var entries = document!.ObjectEntries(field);
                    using var disabled = ImGuiScopes.Disabled(IsAuthoringPipelineUpdating || !document.CanChangeObjectProperty(field));
                    Gui.SetNextItemWidth(-1);
                    if (entries.Count != 0 && BeginNativeCombo("##Type", NativeAbstractEntryControl.Label(entries, unloaded)))
                    {
                        try { NativeAbstractEntryControl.Choices(entries, unloaded, entry => pendingObjectProperty = new(document, field, entry)); }
                        finally { Gui.EndCombo(); }
                    }
                }
                DrawUnloadableProperty(unloaded);
                DrawComponentDefaultAction(field);
            }
            finally { Gui.PopID(); }
            return;
        }
        if (TryDrawRegisteredComponentProperty(field)) { DrawComponentDefaultAction(field); return; }
        if (IsModelNodeName(field)) { DrawModelNodeName(field); DrawComponentDefaultAction(field); return; }
        if (field.IsCategoryHeader && field.Children.Count > 0 && !field.SupportsObjectLifecycle)
        {
            Gui.PushID(field.Name); Gui.SeparatorText(field.Label); DrawComponentFields(field.Children); Gui.PopID(); return;
        }
        if (DrawInheritedComponentField(field)) return;
        DrawComponentFieldValue(field);
        DrawComponentDefaultAction(field);
    }
    private void DrawComponentFieldValue(ComponentField field)
    {
        if (CurveBinding.IsSupported(field.Node.Retrieve()))
        {
            Gui.PushID(field.Name + "/curve");
            if (icons!.Button("edit-curve", "Edit curve", 16)) OpenCurveEditor(document!, field.Node, field.Label);
            Gui.PopID();
        }
        if (field.IsNavigationGroup) { DrawNavigationGroup(field); return; }
        if (EntityHierarchyDocument.IsModelMaterials(field)) { DrawModelMaterialSlots(field); return; }
        if (field.Type == typeof(Stride.Rendering.Compositing.SceneCameraSlotId)) { DrawCameraSlot(field); return; }
        if (field.IsDictionary) { DrawDictionaryProperty(field); return; }
        if (field.IsIndexedCollection) { DrawCollectionProperty(field); return; }
        if (field.IsContentReference) { DrawComponentContentReference(field); return; }
        if (field.Type == typeof(Stride.Core.IO.UFile) || field.Type == typeof(Stride.Core.IO.UDirectory)) { DrawComponentPath(field); return; }
        if (field.IsObjectProperty) { DrawObjectProperty(field); return; }
        using var nullableRow = NativeNullableRow.Begin("##Nullable/" + field.Name, field.IsNullable);
        if (field.IsNullable && !DrawNullableComponentPresence(field))
        {
            nullableRow.ValueColumn(); DrawDocumentedComponentLabel(field); Gui.SameLine(); Gui.TextUnformatted(EditorLocalization.Text("(null)"));
            if (componentErrors.TryGetValue(field, out var nullableError)) DrawWrappedText(nullableError);
            return;
        }
        nullableRow.ValueColumn();
        var value = field.Node.Retrieve();
        if (field.Type == typeof(Stride.Core.Mathematics.Quaternion)) { DrawComponentQuaternion(field); return; }
        if (field.IsSceneReference && field.SupportsEditing) { DrawSceneReference(field); return; }
        if (DrawComponentNativeScalar(field)) return;
        if (field.Type == typeof(string)) { DrawComponentString(field); return; }
        if (AssetSettingNumericMetadata.Supports(field.Type)) { DrawComponentNumeric(field); return; }
        if (field.IsColor) { DrawComponentColor(field); return; }
        if (field.Type == typeof(UInt4) || field.Type == typeof(Matrix) || field.Type == typeof(Rectangle) || field.Type == typeof(RectangleF)) { DrawComponentExtendedMath(field); return; }
        if (field.VectorDimensions != 0) { DrawComponentVector(field); return; }
        if (field.SupportsEditing && field.Type.IsEnum && field.Type.IsDefined(typeof(FlagsAttribute), true))
        { DrawComponentFlags(field); return; }
        if (field.Children.Count != 0)
        {
            Gui.PushID(field.Name);
            ApplyPropertyExpansion(field);
            var open = Gui.TreeNodeEx("##Object", ImGuiTreeNodeFlags.None);
            if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/expand");
            if (Controls.Enabled) Controls.Expanded["component/" + field.Component.Id + "/" + field.Name + "/expand"] = open;
            Gui.SameLine(); DrawDocumentedComponentLabel(field);
            if (Gui.IsItemHovered()) Gui.SetTooltip(value!.GetType().Name);
            if (open)
            {
                DrawComponentFields(field.Children);
                Gui.TreePop();
            }
            Gui.PopID(); return;
        }
        Gui.PushID(field.Name);
        if (!BeginDocumentedComponentRow(field)) { Gui.PopID(); return; }
        Gui.BeginDisabled(!document!.CanEditComponentValue(field) || !field.SupportsEditing);
        var control = Controls.Enabled ? "component/" + field.Component.Id + "/" + field.Name : "";
        var enumControl = field.SupportsEditing && field.Type.IsEnum;
        var discrete = enumControl || field.Type == typeof(bool);
        object? updated = value; var changed = false;
        if (field.Type == typeof(bool)) { var enabled = (bool)(value ?? false); changed = Gui.Checkbox("##Value", ref enabled); updated = enabled; }
        else if (field.Type == typeof(string))
        {
            var text = (string?)value ?? "";
            changed = NativeStringInput.Draw("##Value", ref text, document!.CanEditComponentValue(field) && field.SupportsEditing ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly);
            updated = text;
        }
        else if (enumControl)
        {
            if (!field.IsNullable && icons?.HasEnumImages(field.Type) == true)
            {
                changed = NativeEnumImageControl.Draw(icons, field.Node, field.Type, value, false,
                    field.EnumOptions.Select(option => (Enum)option.Value), UiScale(1), out updated);
                if (Controls.Enabled) Controls.Record(control);
            }
            else
            {
            Gui.PushStyleColor(ImGuiCol.PopupBg, new NVector4(0.055f, 0.08f, 0.12f, 1f));
            var preview = field.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString() ?? "None";
            var comboOpen = Gui.BeginCombo("##Value", preview);
            if (Controls.Enabled) Controls.Record(control);
            if (comboOpen)
            {
                foreach (var option in field.EnumOptions)
                {
                    var selected = Equals(value, option.Value);
                    Gui.PushID(option.Name);
                    if (Gui.Selectable(option.Label, selected)) { updated = option.Value; changed = true; }
                    if (Controls.Enabled) Controls.Record(control + "/option/" + option.Name);
                    if (selected) Gui.SetItemDefaultFocus();
                    Gui.PopID();
                }
                Gui.EndCombo();
            }
            Gui.PopStyleColor();
            }
        }
        else
        {
            var reference = value == null ? null : AttachedReferenceManager.GetAttachedReference(value);
            DrawWrappedText(reference?.Url ?? (value == null ? "None" : field.Type.IsValueType ? value.ToString()! : value.GetType().Name));
        }
        if (Controls.Enabled && !enumControl && (field.EffectiveNumericRange == null || Gui.IsItemVisible()
            && Gui.GetItemRectMax().Y <= Gui.GetWindowPos().Y + Gui.GetWindowSize().Y - Gui.GetStyle().WindowPadding.Y))
        {
            if (Controls.Enabled) Controls.Record(control);
        }
        if (field.SupportsEditing && document.CanEditComponentValue(field))
        {
            if (!discrete && Gui.IsItemActivated()) Execute(() => document.BeginComponentEdit(field));
            if (changed)
            {
                try { document.UpdateComponent(field, updated); componentErrors.Remove(field); }
                catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
            }
            if (discrete ? changed : Gui.IsItemDeactivated()) document.EndComponentEdit();
        }
        if (componentErrors.TryGetValue(field, out var message)) DrawWrappedText(message);
        Gui.EndDisabled(); Gui.EndTable(); Gui.PopID();
    }
}
