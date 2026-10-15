using Hexa.NET.ImGui;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ClearInheritedInspectorState()
    {
        ClearSharedInspectorCaches();
        componentColorHexDrafts.Clear(); commonColorHexDrafts.Clear(); inheritedColorDrafts.Clear(); commonInheritedColorDrafts.Clear(); activeColorPopup = null;
        commonInheritedTransforms = []; inheritedTransformDocument = null;
        inheritedTransformSelection = inheritedTransformStructure = -1;
    }
    private bool DrawInheritedComponentField(ComponentField field, string? label = null)
    {
        if (document == null || field.Node is not IAssetMemberNode { BaseNode: not null } || !SupportsInheritedScalarEditor(field)) return false;
        if (field.IsColor) { DrawComponentColor(field, label); return true; }
        Gui.PushID(field.Name); DrawInheritedPropertyLabel(field, label); DrawInheritedResetAction(field);
        var owner = document;
        var value = field.Node.Retrieve(); var type = field.Type;
        using var nullableRow = NativeNullableRow.Begin("##Nullable", field.IsNullable);
        if (field.IsNullable)
        {
            var present = value != null;
            using (ImGuiScopes.Disabled(!owner.CanOverrideInheritedComponent(field)))
                if (DrawNativeNullablePresence(present, out var create, field.AllowsNullObject)) Apply(create ? CreateNullableValue(type) : null);
            value = field.Node.Retrieve();
            nullableRow.ValueColumn();
            if (value == null) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); DrawInheritedResetAction(field); nullableRow.Dispose(); Gui.PopID(); return true; }
        }
        if (field.IsNavigationGroup)
        {
            DrawNavigationGroupValue(value, false, document.CanOverrideInheritedComponent(field), updated => Apply(updated));
            DrawInheritedResetAction(field);
        }
        else if (type == typeof(string)) { DrawInheritedString(field); DrawInheritedResetAction(field); }
        else if (IsTemporalType(type))
        {
            DrawRetainedTemporal(field.Node, type,
                () => ReferenceEquals(document, owner) && owner.CanOverrideInheritedComponent(field), () => [field.Node.Retrieve()],
                transform => owner.EditInheritedScalar(field, transform(field.Node.Retrieve())), 1, () => AcquirePropertyStringOwners([owner]));
            DrawInheritedResetAction(field);
        }
        else if (type == typeof(char))
        {
            DrawRetainedCharacter(field.Node, () => [field.Node.Retrieve()],
                () => ReferenceEquals(document, owner) && owner.CanOverrideInheritedComponent(field),
                character => owner.EditInheritedScalar(field, character), () => AcquirePropertyStringOwners([owner]));
            DrawInheritedResetAction(field);
        }
        else if (type == typeof(Guid))
        {
            DrawRetainedGuid(field.Node, () => [field.Node.Retrieve()],
                () => ReferenceEquals(document, owner) && owner.CanOverrideInheritedComponent(field),
                guid => owner.EditInheritedScalar(field, guid), () => AcquirePropertyStringOwners([owner]));
            DrawInheritedResetAction(field);
        }
        else if (type == typeof(AngleSingle))
        {
            DrawRetainedAngle(field.Node, () => [field.Node.Retrieve()],
                () => ReferenceEquals(document, owner) && owner.CanOverrideInheritedComponent(field),
                angle => owner.EditInheritedScalar(field, angle), () => AcquirePropertyStringOwners([owner]));
            DrawInheritedResetAction(field);
        }
        else if (DrawNativeScalarTemplate(field.Node, type, value, false, document.CanOverrideInheritedComponent(field), Apply))
            DrawInheritedResetAction(field);
        else if (type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory))
        {
            DrawEntityPropertyPaths(type, [owner], [(Func<object?>)(() => field.Node.Retrieve())],
                () => owner.CanOverrideInheritedComponent(field), path => owner.EditInheritedScalar(field, path));
            DrawInheritedResetAction(field);
        }
        else if (type == typeof(bool))
        {
            using (ImGuiScopes.Disabled(!owner.CanOverrideInheritedComponent(field)))
            {
                var next = (bool)(value ?? false); if (Gui.Checkbox("##Value", ref next)) Apply(next);
            }
            DrawInheritedResetAction(field);
        }
        else if (type.IsEnum)
        {
            using (ImGuiScopes.Disabled(!owner.CanOverrideInheritedComponent(field)))
            {
            Gui.SetNextItemWidth(-1);
            if (DrawComponentEnumArtwork(field, value, false, Apply)) { }
            else if (Gui.BeginCombo("##Value", field.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString()))
            {
                foreach (var option in field.EnumOptions)
                {
                    if (type.IsDefined(typeof(FlagsAttribute), true))
                    {
                        var bits = EnumBits(value ?? Enum.ToObject(type, 0)); var mask = EnumBits(option.Value); var enabled = mask == 0 ? bits == 0 : (bits & mask) == mask;
                        if (Gui.Checkbox(option.Label, ref enabled)) { value = EnumFromBits(type, mask == 0 ? 0 : enabled ? bits | mask : bits & ~mask); Apply(value); }
                    }
                    else if (Gui.Selectable(option.Label, Equals(value, option.Value))) Apply(option.Value);
                }
                if (type.IsDefined(typeof(FlagsAttribute), true) && DrawFlagBulkActions() is { } action)
                    Apply(ApplyFlagBulkAction((Enum)(field.Node.Retrieve() ?? Enum.ToObject(type, 0)), action));
                Gui.EndCombo();
            }
            }
            DrawInheritedResetAction(field);
        }
        else if (type == typeof(Quaternion))
        {
            var rotationView = Gui.GetID("##Rotation");
            for (var axis = 0; axis < 3; axis++)
            {
                Gui.PushID(axis); Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine();
                if (field.Component is TransformComponent) DrawInheritedTransformCoordinate(field, axis, rotationView);
                else { DrawComponentCoordinateDraft(field, axis, "##Value", RotationEditor(field.Node, rotationView), inherited: true); DrawInheritedResetAction(field); }
                Gui.PopID();
            }
        }
        else if (CommonValueDimensions(type) is var dimensions && dimensions > 0)
        {
            var matrix = UsesExtendedMathTable(type) && Gui.BeginTable("##Matrix", 4, ImGuiTableFlags.SizingStretchSame);
            for (var axis = 0; axis < dimensions && (!UsesExtendedMathTable(type) || matrix); axis++)
            {
                Gui.PushID(axis);
                DrawExtendedMathCoordinateLabel(type, axis, matrix);
                if (field.Component is TransformComponent) DrawInheritedTransformCoordinate(field, axis);
                else { DrawComponentCoordinateDraft(field, axis, "##Value", inherited: true); DrawInheritedResetAction(field); }
                Gui.PopID();
            }
            if (matrix) Gui.EndTable();
        }
        else if (AssetSettingNumericMetadata.Supports(type)) DrawInheritedNumericDraft(field);
        else Gui.TextUnformatted(value?.ToString() ?? "None");
        if (componentErrors.TryGetValue(field, out var error)) DrawSelectableText("##InheritedError", error);
        nullableRow.Dispose(); Gui.PopID(); return true;
        void Apply(object? next)
        {
            try { if (!ReferenceEquals(document, owner) || !owner.CanOverrideInheritedComponent(field)) throw new InvalidOperationException("The property is no longer editable."); owner.EditInheritedScalar(field, next); componentErrors.Remove(field); }
            catch (Exception failure) { componentErrors[field] = failure.Message; statusMessage = failure.Message; }
        }
    }
    private void DrawInheritedPropertyLabel(ComponentField field, string? label)
    {
        var node = (IAssetMemberNode)field.Node; var overridden = node.IsContentOverridden();
        var bold = PushPackagePropertiesBoldFont(overridden);
        if (!overridden)
        {
            var color = Gui.GetStyle().Colors[(int)ImGuiCol.Text]; color.W *= 0.5f; Gui.PushStyleColor(ImGuiCol.Text, color);
        }
        DrawDocumentedComponentLabel(field, label);
        if (!overridden) Gui.PopStyleColor();
        if (bold) Gui.PopFont();
    }
    private void DrawInheritedResetAction(ComponentField field)
    {
        if (!Gui.BeginPopupContextItem("ResetInherited")) return;
        DrawPropertyClipboardCommands(document!, field);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, document!.CanResetInheritedScalarBatch([field])))
            Execute(() =>
            {
                ValidatePendingPropertyStrings();
                document.ResetInheritedScalar(field); componentErrors.Remove(field); inheritedColorDrafts.Remove(field); componentColorHexDrafts.Remove(field);
            });
        DrawRegisteredPropertyCommands(document!, field.Node, document!.CanEditExtensionProperty(field), document.ApplyInheritedGraphMutation);
        Gui.EndPopup();
    }
    private void DrawInheritedTransform(TransformComponent transform)
    {
        foreach (var field in document!.GetComponentFields(transform))
            if (field.Node.Name is nameof(TransformComponent.Position) or nameof(TransformComponent.Rotation) or nameof(TransformComponent.Scale))
            {
                if (!DrawInheritedComponentField(field))
                    DrawComponentField(field);
            }
    }
}
