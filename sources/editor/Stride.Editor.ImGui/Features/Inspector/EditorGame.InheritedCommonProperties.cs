using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IReadOnlyList<CommonComponentProperty> commonInheritedTransforms = [];
    private EntityHierarchyDocument? inheritedTransformDocument;
    private long inheritedTransformSelection = -1, inheritedTransformStructure = -1;
    private bool CanEditInheritedCommon(CommonComponentProperty property) => document is { SelectedCount: > 1 } owner
        && property.Fields.Count == owner.SelectedCount && property.Fields.All(member => member.Component.Entity is { } entity && owner.IsSelected(entity.Id)
            && SupportsInheritedScalarEditor(member)) && owner.CanEditInheritedScalarBatch(property.Fields);
    private bool CanShowInheritedCommon(CommonComponentProperty property) => document is { SelectedCount: > 1 } owner
        && property.Fields.Count == owner.SelectedCount
        && property.Fields.Any(member => member.Node is IAssetMemberNode { BaseNode: not null })
        && property.Fields.All(member => member.Component.Entity is { } entity && owner.IsSelected(entity.Id) && SupportsInheritedScalarEditor(member));
    private static bool SupportsInheritedScalarEditor(ComponentField field) => !field.IsSceneReference && !field.IsContentReference
        && (field.Type.IsPrimitive || field.Type.IsEnum || field.Type == typeof(string) || field.Type == typeof(decimal)
            || field.Type == typeof(Stride.Core.IO.UFile) || field.Type == typeof(Stride.Core.IO.UDirectory)
            || field.Type == typeof(AngleSingle) || field.Type == typeof(Guid) || field.Type == typeof(TimeSpan) || field.Type == typeof(DateTime)
            || field.Type == typeof(Quaternion) || CommonValueDimensions(field.Type) > 0);
    private void DrawInheritedCommonTransform()
    {
        var owner = document!;
        if (inheritedTransformDocument != owner || inheritedTransformSelection != owner.SelectionVersion || inheritedTransformStructure != owner.StructureVersion)
        {
            inheritedTransformDocument = owner; inheritedTransformSelection = owner.SelectionVersion; inheritedTransformStructure = owner.StructureVersion;
            var transforms = owner.SelectedIds.Select(id => owner.Asset.Hierarchy.Parts[id].Entity.Transform).ToArray();
            var properties = new List<CommonComponentProperty>();
            foreach (var name in new[] { nameof(TransformComponent.Position), nameof(TransformComponent.Rotation), nameof(TransformComponent.Scale) })
            {
                var members = transforms.Select(transform => owner.GetComponentFields(transform).FirstOrDefault(member => member.Node.Name == name)).ToArray();
                if (members.Any(member => member == null)) continue;
                properties.Add(new CommonComponentProperty("InheritedTransform/" + name, "Transform", members.Cast<ComponentField>().ToArray()));
            }
            commonInheritedTransforms = properties;
        }
        if (!commonInheritedTransforms.Any(CanShowInheritedCommon)) return;
        if (Gui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            foreach (var property in commonInheritedTransforms) DrawInheritedCommonProperty(property);
    }
    private void DrawInheritedCommonProperty(CommonComponentProperty property)
    {
        var field = property.First; var type = field.Type; var value = property.Value; var mixed = property.IsMixed;
        uint? conversionFeedback = null;
        Gui.PushID(property.Key); DrawInheritedCommonLabel(property); DrawInheritedCommonReset(property);
        if (property.First.IsColor) { DrawRetainedCombinedColor(property, inherited: true); Gui.PopID(); return; }
        if (UsesCombinedNumericDraft(property)) { DrawCombinedNumericDrafts(property, inherited: true); Gui.PopID(); return; }
        if (type == typeof(string)) { DrawCommonString(property, true); DrawInheritedCommonReset(property); Gui.PopID(); return; }
        Gui.BeginDisabled(!CanEditInheritedCommon(property));
        using var nullableRow = NativeNullableRow.Begin("##Nullable", field.IsNullable);
        if (field.IsNullable)
        {
            var present = property.Fields.Any(member => member.Node.Retrieve() != null);
            if (DrawNativeNullablePresence(present, out var create, property.Fields.All(member => member.AllowsNullObject))) Apply(member => create
                ? member.Node.Retrieve() ?? CreateNullableValue(type) : null);
            nullableRow.ValueColumn(); DrawInheritedCommonReset(property);
            if (property.Fields.All(member => member.Node.Retrieve() == null)) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); nullableRow.Dispose(); Gui.EndDisabled(); DrawInheritedCommonReset(property); Gui.PopID(); return; }
            value = property.Value; mixed = property.IsMixed;
        }
        if (field.IsColor)
            DrawInheritedCommonColor(property, create => Apply(create), () => DrawInheritedCommonReset(property));
        else if (field.IsNavigationGroup)
        {
            DrawNavigationGroupValue(value, mixed, CanEditInheritedCommon(property), updated => Apply(_ => updated));
            DrawInheritedCommonReset(property);
        }
        else if (type == typeof(string)) { DrawCommonString(property, true); DrawInheritedCommonReset(property); }
        else if (DrawNativeScalarTemplate(field.Node, type, value, mixed, CanEditInheritedCommon(property), updated => Apply(_ => updated)))
            DrawInheritedCommonReset(property);
        else if (type == typeof(bool))
        {
            var next = (bool)(value ?? false); Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
            if (Gui.Checkbox("##Value", ref next)) Apply(_ => next); Gui.PopItemFlag(); DrawInheritedCommonReset(property);
        }
        else if (type.IsEnum)
        {
            Gui.SetNextItemWidth(-1);
            if (DrawComponentEnumArtwork(field, value, mixed, updated => Apply(_ => updated))) { }
            else if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : field.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString()))
            {
                foreach (var option in field.EnumOptions)
                {
                    if (type.IsDefined(typeof(FlagsAttribute), true))
                    {
                        var mask = EnumBits(option.Value); var bits = EnumBits(value ?? Enum.ToObject(type, 0));
                        var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                        var flagMixed = property.Fields.Skip(1).Any(member =>
                        {
                            var current = EnumBits(member.Node.Retrieve() ?? Enum.ToObject(type, 0));
                            return (mask == 0 ? current == 0 : (current & mask) == mask) != selected;
                        });
                        Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, flagMixed);
                        if (Gui.Checkbox(option.Label, ref selected)) Apply(member =>
                        {
                            var current = EnumBits(member.Node.Retrieve() ?? Enum.ToObject(type, 0));
                            return EnumFromBits(type, mask == 0 ? 0 : selected ? current | mask : current & ~mask);
                        });
                        Gui.PopItemFlag();
                    }
                    else if (Gui.Selectable(option.Label, !mixed && Equals(value, option.Value))) Apply(_ => option.Value);
                }
                if (type.IsDefined(typeof(FlagsAttribute), true) && DrawFlagBulkActions() is { } action)
                    Apply(member => ApplyFlagBulkAction((Enum)(member.Node.Retrieve() ?? Enum.ToObject(type, 0)), action));
                Gui.EndCombo();
            }
            DrawInheritedCommonReset(property);
        }
        else if (field.IsColor)
        {
            DrawInheritedCommonColor(property);
        }
        else if (type == typeof(Quaternion))
        {
            var rotationView = Gui.GetID("##Rotation");
            for (var axis = 0; axis < 3; axis++)
            {
                var index = axis; Gui.PushID(axis); Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine();
                var coordinate = RotationCoordinate(field, rotationView, axis);
                var axisMixed = property.Fields.Skip(1).Any(member => RotationCoordinate(member, rotationView, axis) != coordinate);
                Text(axis, coordinate, axisMixed, text => Apply(member => ReplaceRotationCoordinate(member, rotationView, index,
                    float.Parse(text, CultureInfo.InvariantCulture)))); Gui.PopID();
            }
        }
        else if (CommonValueDimensions(type) is var dimensions && dimensions > 0)
        {
            var matrix = UsesExtendedMathTable(type) && Gui.BeginTable("##Matrix", 4, ImGuiTableFlags.SizingStretchSame);
            for (var axis = 0; axis < dimensions && (!UsesExtendedMathTable(type) || matrix); axis++)
            {
                var index = axis; Gui.PushID(axis);
                DrawExtendedMathCoordinateLabel(type, axis, matrix);
                var coordinate = CommonCoordinate(value, axis);
                var axisMixed = property.Fields.Skip(1).Any(member => !Equals(CommonCoordinate(member.Node.Retrieve(), axis), coordinate));
                Text(axis, coordinate, axisMixed, text => Apply(member => WithCommonCoordinate(member.Node.Retrieve(), type, index,
                    type == typeof(UInt4) ? uint.Parse(text, CultureInfo.InvariantCulture) : field.IsIntegerVector || type == typeof(Rectangle)
                        ? (object)int.Parse(text, CultureInfo.InvariantCulture) : float.Parse(text, CultureInfo.InvariantCulture))));
                Gui.PopID();
            }
            if (matrix) Gui.EndTable();
        }
        else if (type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory)) DrawCommonPropertyPath(property, true);
        else Text(-1, value, mixed, text => Apply(_ => type == typeof(string) ? text : type == typeof(decimal)
            ? decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture) : Convert.ChangeType(text, type, CultureInfo.InvariantCulture)));
        nullableRow.Dispose(); Gui.EndDisabled(); DrawInheritedCommonReset(property); Gui.PopID();
        void Apply(Func<ComponentField, object?> create)
        {
            var feedback = conversionFeedback;
            Execute(() => document!.EditInheritedScalars(property.Fields, property.Fields.Select(member => feedback is { } id
                ? PropertyConversionFeedback.Convert(id, () => create(member)) : create(member)).ToArray()));
        }
        void Text(int axis, object? current, bool different, Action<string> edit)
        {
            using var axisDisabled = ImGuiScopes.Disabled(axis >= 0 && property.Fields.Any(field => field.IsAxisReadOnly(axis)));
            var key = (property.Key, axis); var text = commonPropertyDrafts.GetValueOrDefault(key) ?? (different ? "" : Convert.ToString(current, CultureInfo.InvariantCulture) ?? "");
            var editable = CanEditInheritedCommon(property);
            var readonlyString = type == typeof(string) && !editable;
            if (readonlyString) { commonPropertyDrafts.Remove(key); text = different ? "" : (string?)current ?? ""; }
            Gui.SetNextItemWidth(-1);
            var submitted = DrawRangedPropertyText(property.Fields, axis, current, different, ref text);
            submitted |= NativeStringInput.DrawWithHint("##Value", different ? DifferentPropertyValues : "", ref text, ImGuiInputTextFlags.EnterReturnsTrue | (readonlyString ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None));
            submitted |= StepPropertyText(property.Fields, axis, current, different, ref text);
            var active = Gui.IsItemActive(); var ended = Gui.IsItemDeactivatedAfterEdit();
            if (editable && active) commonPropertyDrafts[key] = text; else commonPropertyDrafts.Remove(key);
            var feedback = Gui.GetID("##Value");
            if (editable && (submitted || ended))
            {
                conversionFeedback = feedback;
                try { Execute(() => PropertyConversionFeedback.Run(feedback, () => edit(text))); }
                finally { conversionFeedback = null; }
            }
            PropertyConversionFeedback.Draw(feedback);
            DrawInheritedCommonReset(property);
        }
    }
    private void DrawInheritedCommonLabel(CommonComponentProperty property)
    {
        var overridden = property.Fields.Any(member => member.Node is IAssetMemberNode node && node.IsContentOverridden());
        var bold = PushPackagePropertiesBoldFont(overridden);
        if (!overridden) { var color = Gui.GetStyle().Colors[(int)ImGuiCol.Text]; color.W *= 0.5f; Gui.PushStyleColor(ImGuiCol.Text, color); }
        Gui.TextUnformatted(property.First.Label);
        if (!overridden) Gui.PopStyleColor(); if (bold) Gui.PopFont();
    }
    private void DrawInheritedCommonReset(CommonComponentProperty property)
    {
        if (!Gui.BeginPopupContextItem("ResetInherited")) return;
        DrawCommonPropertyClipboardCommands(property);
        if (property.First.Type.IsEnum && property.First.Type.IsDefined(typeof(FlagsAttribute), true))
        {
            using var flagsDisabled = ImGuiScopes.Disabled(!CanEditInheritedCommon(property));
            if (DrawFlagBulkActions() is { } action)
                Execute(() => document!.EditInheritedScalars(property.Fields, property.Fields.Select(member =>
                    (object?)ApplyFlagBulkAction((Enum)(member.Node.Retrieve() ?? Enum.ToObject(member.Type, 0)), action)).ToArray()));
        }
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, document!.CanResetInheritedScalarBatch(property.Fields)
            && property.Fields.All(SupportsInheritedScalarEditor)))
            Execute(() => { ValidatePendingPropertyStrings(); document!.ResetInheritedScalars(property.Fields); commonInheritedColorDrafts.Remove(property.Key); commonColorHexDrafts.Remove(property.Key); foreach (var key in commonPropertyDrafts.Keys.Where(key => key.Property == property.Key).ToArray()) commonPropertyDrafts.Remove(key); });
        Gui.EndPopup();
    }
}
