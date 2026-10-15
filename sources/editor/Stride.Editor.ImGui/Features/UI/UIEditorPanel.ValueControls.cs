using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.IO;
using Stride.Core.Assets;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.UI;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private static ulong Bits(object value) => Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
    {
        TypeCode.SByte => unchecked((byte)Convert.ToSByte(value)),
        TypeCode.Int16 => unchecked((ushort)Convert.ToInt16(value)),
        TypeCode.Int32 => unchecked((uint)Convert.ToInt32(value)),
        TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(value)),
        _ => Convert.ToUInt64(value),
    };

    private void DrawEnumProperty(UIElementProperty field, object? value, bool mixed, bool canEdit)
    {
        var source = document!; var ids = selected.ToArray();
        DrawUIValue(field.Name, field.Label, field.Type, value,
            ids.Select(id => source.Properties(id).Single(other => other.Name == field.Name).Node.Retrieve()).ToArray(),
            canEdit, field.AllowsNull, change => source.TransformProperties(ids, field.Name, change),
            valueTarget: MemberValueTarget(source, ids, field.Name));
    }

    private void DrawObjectProperty(UIElementProperty field, object? value, bool mixed)
    {
        var source = document!; var ids = selected.ToArray();
        var fields = ids.Select(id => source.Properties(id).Single(other => other.Name == field.Name)).ToArray();
        var entries = source.CommonUIPropertyEntries(ids, field.Name);
        var canEdit = fields.All(source.CanEdit);
        using var disabled = ImGuiScopes.Disabled(!canEdit);
        if (!Gui.BeginCombo("##Value", mixed ? DifferentValues : NativeAbstractEntryControl.Label(entries, value))) return;
        try
        {
            NativeAbstractEntryControl.Choices(entries, value, entry => host.SelectUIFactoryEntry(source, ids, field.Name, entry));
        }
        finally { Gui.EndCombo(); }
    }

    private void DrawUIValue(string identity, string label, Type declaredType, object? value, IReadOnlyList<object?> values,
        bool canEdit, bool allowsNull, Action<Func<object?, object?>> transform, PropertyNumericRange? numericRange = null,
        IReadOnlyList<Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry>? nativeEntries = null,
        Action<Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry>? selectEntry = null,
        Func<PropertyNumericRange?>? numericRangeProvider = null,
        Func<int, PropertyNumericRange?>? axisNumericRangeProvider = null,
        Func<int, bool>? axisCanEditProvider = null,
        Func<IReadOnlyList<PropertyNumericRange?>>? rangedRangesProvider = null, UIValueTarget? valueTarget = null)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        var mixed = values.Skip(1).Any(other => !ContentAssetReference.AreEqual(value, other));
        if (nativeEntries == null && !ContentAssetReference.Supports(type) && values.Where(current => current != null).Select(current => current!.GetType()).Distinct().Skip(1).Any())
        { host.DrawSelectableText("##Value", DifferentValues); return; }
        void Commit(object? next) => transform(_ => next);
        if (nativeEntries != null && Nullable.GetUnderlyingType(declaredType) == null)
        {
            using var disabled = ImGuiScopes.Disabled(!canEdit);
            if (Gui.BeginCombo("##Value", mixed ? DifferentValues : NativeAbstractEntryControl.Label(nativeEntries, value)))
            {
                try { NativeAbstractEntryControl.Choices(nativeEntries, value, entry =>
                { if (selectEntry != null) selectEntry(entry); else transform(current => entry.IsMatchingValue(current!) ? current : entry.GenerateValue(current!)); }); }
                finally { Gui.EndCombo(); }
            }
            return;
        }
        using var nullableRow = NativeNullableRow.Begin("##Nullable", Nullable.GetUnderlyingType(declaredType) != null);
        if (Nullable.GetUnderlyingType(declaredType) != null)
        {
            var allNull = values.All(current => current == null);
            {
                using var disabled = ImGuiScopes.Disabled(!canEdit || !allNull && !allowsNull);
                if (host.DrawNullablePresence(!allNull, out var create))
                {
                    if (create) transform(_ => ObjectFactoryRegistry.NewInstance(type)); else Commit(null);
                }
            }
            nullableRow.ValueColumn();
            if (allNull) { host.DrawSelectableText("##Value", EditorLocalization.Text("(null)")); return; }
        }
        if (value is Stride.Core.Yaml.IUnloadable unloaded) { host.DrawUnloadable(unloaded); return; }
        if (host.DrawPrimitive(declaredType, value, label, canEdit, Commit, () => { })) return;
        if (ContentAssetReference.Supports(type))
        {
            if (canEdit) host.DrawReference(type, value, Commit, mixed, allowsNull);
            else host.DrawSelectableText("##Value", mixed ? DifferentValues : ContentAssetReference.Read(value)?.Location ?? "");
            return;
        }
        if (type == typeof(DateTime))
        {
            if (valueTarget is { } target)
            {
                host.DrawRetainedTemporalProperty(target.Source, target.Identity, type, target.CanEdit, target.Read,
                    target.Transform, values.Count, target.Acquire);
                return;
            }
            host.DrawDateTime(document!, value is DateTime date ? date : default, mixed, canEdit, Commit);
            return;
        }
        if (type == typeof(TimeSpan))
        {
            if (valueTarget is { } target)
            {
                host.DrawRetainedTemporalProperty(target.Source, target.Identity, type, target.CanEdit, target.Read,
                    target.Transform, values.Count, target.Acquire);
                return;
            }
            if (NativeTimeSpanInput.Draw(document!, value is TimeSpan duration ? duration : default, mixed, canEdit, out var updated, out _)) Commit(updated);
            return;
        }
        if (type == typeof(Guid))
        {
            if (valueTarget is { } target)
            {
                host.DrawRetainedGuidProperty(target.Source, target.Identity, target.CanEdit, target.Read,
                    replacement => target.Transform(_ => replacement), values.Count, target.Acquire);
                return;
            }
            if (NativeGuidInput.Draw(document!, value is Guid guid ? guid : default, mixed, canEdit, out var updated)) Commit(updated);
            return;
        }
        if (type == typeof(char))
        {
            if (valueTarget is { } target)
            {
                host.DrawRetainedCharacterProperty(target.Source, target.Identity, target.CanEdit, target.Read,
                    replacement => target.Transform(_ => replacement), values.Count, target.Acquire);
                return;
            }
            if (NativeCharacterInput.Draw(document!, value is char character ? character : default, mixed, canEdit, Gui.GetStyle().FontScaleDpi, out var updated)) Commit(updated);
            return;
        }
        if (type == typeof(AngleSingle))
        {
            if (valueTarget is { } target)
            {
                host.DrawRetainedAngleProperty(target.Source, target.Identity, target.CanEdit, target.Read,
                    replacement => target.Transform(_ => replacement), values.Count, target.Acquire);
                return;
            }
            if (NativeAngleInput.Draw(document!, value is AngleSingle angle ? angle : default, mixed, canEdit, out var updated)) Commit(updated);
            return;
        }
        if (type == typeof(bool))
        {
            using var disabled = ImGuiScopes.Disabled(!canEdit); using var different = new MixedValueScope(mixed);
            var selectedValue = value is true;
            if (Gui.Checkbox("##Value", ref selectedValue)) Commit(selectedValue);
            return;
        }
        if (type.IsEnum)
        {
            using var disabled = ImGuiScopes.Disabled(!canEdit);
            if (host.DrawEnumImages(document!, type, value, mixed, Commit)) return;
            if (!Gui.BeginCombo("##Value", mixed ? DifferentValues : value?.ToString() ?? "")) return;
            try
            {
                var flags = type.IsDefined(typeof(FlagsAttribute), true);
                foreach (var name in Enum.GetNames(type))
                {
                    var option = Enum.Parse(type, name);
                    var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(type.GetField(name)!);
                    var optionLabel = display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(name);
                    if (!flags) { if (Gui.Selectable(optionLabel, !mixed && Equals(value, option))) Commit(option); continue; }
                    var mask = Bits(option); var bits = Bits(value ?? Enum.ToObject(type, 0));
                    var enabled = mask == 0 ? bits == 0 : (bits & mask) == mask;
                    var different = values.Skip(1).Any(current => { var actual = Bits(current ?? Enum.ToObject(type, 0)); return (mask == 0 ? actual == 0 : (actual & mask) == mask) != enabled; });
                    using var mixedFlag = new MixedValueScope(different);
                    if (Gui.Checkbox(optionLabel, ref enabled)) transform(current =>
                    { var actual = Bits(current ?? Enum.ToObject(type, 0)); return Enum.ToObject(type, mask == 0 ? 0UL : enabled ? actual | mask : actual & ~mask); });
                }
                if (flags && FlagEnumPropertyControl.DrawBulkActions() is { } action)
                    transform(current => FlagEnumSelection.Apply((Enum)(current ?? Enum.ToObject(type, 0)), action));
            }
            finally { Gui.EndCombo(); }
            return;
        }
        var compoundValue = value ?? values.FirstOrDefault(current => current != null);
        if (type == typeof(Quaternion) && compoundValue is Quaternion)
        { DrawUIQuaternion(identity, compoundValue, values, canEdit, transform, valueTarget); return; }
        if (compoundValue is Vector2 or Vector3 or Vector4 or Int2 or Int3 or Int4 or UInt4 or Thickness or Matrix or Rectangle or RectangleF)
        { DrawUICoordinates(identity, compoundValue, values, canEdit, transform, axisNumericRangeProvider, axisCanEditProvider, valueTarget); return; }
        if (type == typeof(Color) || type == typeof(Color3) || type == typeof(Color4))
        {
            if (valueTarget is { } target)
            {
                host.DrawRetainedColorProperty(target.Source, target.Identity, type, target.CanEdit, target.Read,
                    target.Transform, values.Count, target.CanEditAxis, target.Acquire);
                return;
            }
            throw new InvalidOperationException("The color field has no canonical UI value target.");
        }
        if (AssetSettingNumericMetadata.Supports(type) && valueTarget is { } numericTarget)
        {
            host.DrawRetainedNumericProperty(numericTarget.Source, numericTarget.Identity, type, value, mixed,
                numericTarget.CanEdit, numericTarget.Read, next => numericTarget.Transform(_ => next),
                () => numericRangeProvider?.Invoke() ?? numericRange,
                () => rangedRangesProvider?.Invoke() ?? (numericRange != null ? [numericRange] : Array.Empty<PropertyNumericRange?>()), numericTarget.Acquire);
            return;
        }
        if ((type == typeof(UFile) || type == typeof(UDirectory)) && valueTarget is { } pathTarget)
        {
            host.DrawRetainedPathProperty(pathTarget.Source, pathTarget.Identity, type, pathTarget.CanEdit, pathTarget.Read,
                replacement => pathTarget.Transform(_ => replacement), values.Count, pathTarget.Acquire);
            return;
        }
        if (type == typeof(string) || type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid) || type == typeof(TimeSpan)
            || type == typeof(UFile) || type == typeof(UDirectory))
        {
            var draftKey = "value/" + PropertyDraftKey(identity);
            var display = numericRange?.Format is { } format && value is IFormattable formatted
                ? formatted.ToString("F" + format[2..^1], CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            var text = drafts.GetValueOrDefault(draftKey, mixed ? "" : display);
            var sliderCommitted = false;
            IReadOnlyList<PropertyNumericRange?> sliderRanges = rangedRangesProvider?.Invoke() ?? (numericRange != null ? [numericRange] : Array.Empty<PropertyNumericRange?>());
            if (NativeRangedNumericControl.Matches(declaredType, sliderRanges.FirstOrDefault()))
            {
                using var sliderDisabled = ImGuiScopes.Disabled(!canEdit);
                sliderCommitted = NativeRangedNumericControl.Draw(document!, type, value, mixed, sliderRanges, Gui.GetStyle().FontScaleDpi, out var sliderValue, out _);
                if (sliderValue != null && (mixed || !Equals(sliderValue, value))) text = Convert.ToString(sliderValue, CultureInfo.InvariantCulture) ?? "";
            }
            var submitted = sliderCommitted;
            submitted |= NativeStringInput.DrawWithHint("##Value", mixed ? DifferentValues : "", ref text,
                ImGuiInputTextFlags.EnterReturnsTrue | (canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
            var stepped = canEdit && NativeNumericInput.TryGesture(type, null, out _) && NativeNumericInput.StepText(type, value, mixed,
                numericRangeProvider?.Invoke() ?? numericRange, ref text);
            submitted |= stepped;
            var feedback = Gui.GetID("##Value");
            if ((type == typeof(UFile) || type == typeof(UDirectory)) && document is { } source)
            {
                var selection = selected.ToArray();
                host.RegisterPathDrop(source, type, source.EditRevision,
                    () => canEdit && ReferenceEquals(document, source) && selected.SetEquals(selection),
                    path => { Commit(path); drafts.Remove(draftKey); });
            }
            if (canEdit && (Gui.IsItemEdited() || stepped || sliderCommitted)) drafts[draftKey] = text;
            if (canEdit && (submitted || Gui.IsItemDeactivatedAfterEdit()) && drafts.ContainsKey(draftKey))
            {
                try
                {
                    object? next = type == typeof(string) ? text : type == typeof(Guid) ? Guid.Parse(text)
                        : type == typeof(TimeSpan) ? TimeSpan.Parse(text, CultureInfo.InvariantCulture)
                        : type == typeof(UFile) ? new UFile(text) : type == typeof(UDirectory) ? new UDirectory(text)
                        : Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                    Commit(next); drafts.Remove(draftKey); PropertyConversionFeedback.Validate(feedback);
                }
                catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); host.ReportValidationFailure(error); }
            }
            PropertyConversionFeedback.Draw(feedback);
            return;
        }
        using (ImGuiScopes.Disabled(!canEdit))
        {
            if (Gui.BeginCombo("##Value", mixed ? DifferentValues : value == null ? "None" : DisplayAttribute.GetDisplayName(value.GetType()) ?? value.GetType().Name))
            {
                try
                {
                    if (allowsNull && !declaredType.IsValueType && Gui.Selectable(EditorLocalization.Widget("None", "Button"), !mixed && value == null)) Commit(null);
                    var content = AssetRegistry.CanPropertyHandleContent(type, out var registered) ? registered : [];
                    foreach (var candidate in type.GetInheritedInstantiableTypes().Append(type).Distinct().Where(candidate =>
                        !candidate.IsAbstract && !candidate.IsInterface && !typeof(UIElement).IsAssignableFrom(candidate)
                        && !content.Contains(candidate) && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NonInstantiableAttribute>(candidate) == null
                        && (candidate.IsValueType || ObjectFactoryRegistry.CanCreateInstance(candidate))))
                    {
                        if (Gui.Selectable(DisplayAttribute.GetDisplayName(candidate) ?? candidate.Name, !mixed && value?.GetType() == candidate))
                            transform(_ => ObjectFactoryRegistry.NewInstance(candidate));
                    }
                }
                finally { Gui.EndCombo(); }
            }
        }
    }
    private void DrawUICoordinates(string identity, object value, IReadOnlyList<object?> values, bool canEdit, Action<Func<object?, object?>> transform,
        Func<int, PropertyNumericRange?>? axisNumericRangeProvider = null, Func<int, bool>? axisCanEditProvider = null, UIValueTarget? valueTarget = null)
    {
        var dimensions = value switch { Vector2 or Int2 => 2, Vector3 or Int3 => 3, Matrix => 16, Thickness => 6, _ => 4 };
        if (!Gui.BeginTable("##Coordinates", value is Thickness ? 2 : value is Matrix ? 4 : dimensions, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < dimensions; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    var coordinate = UICoordinate(value, axis); var index = axis;
                    // Child-bound math templates have a common axis only when
                    // every selected parent supplies a real coordinate target.
                    var axisCanEdit = canEdit && values.All(current => current != null) && axisCanEditProvider?.Invoke(axis) != false;
                    var mixed = values.Any(current => current == null) || values.Skip(1).Any(current => !Equals(UICoordinate(current!, axis), coordinate));
                    var draftKey = "coordinate/" + PropertyDraftKey(identity) + "/" + axis;
                    var text = drafts.GetValueOrDefault(draftKey, mixed ? "" : Convert.ToString(coordinate, CultureInfo.InvariantCulture) ?? "");
                    Gui.TextUnformatted(value is Thickness ? new[] { "L", "R", "T", "B", "Back", "Front" }[axis] : value is Matrix ? "M" + (axis / 4 + 1) + (axis % 4 + 1)
                        : value is Rectangle or RectangleF ? "XYWH"[axis].ToString() : "XYZW"[axis].ToString());
                    Gui.SameLine(); Gui.SetNextItemWidth(-float.Epsilon);
                    if (valueTarget is { } target)
                    {
                        host.DrawRetainedNumericProperty(target.Source, target.Identity, coordinate.GetType(), coordinate, mixed,
                            () => target.CanEdit() && target.Read().Take(values.Count).All(current => current != null)
                                && (target.CanEditAxis ?? axisCanEditProvider)?.Invoke(index) != false,
                            target.Read, replacement => target.Transform(current => WithUICoordinate(current
                                ?? throw new InvalidOperationException("The property no longer supplies a coordinate target."), index, replacement!)),
                            () => (target.AxisRange ?? axisNumericRangeProvider) is { } range ? range(index)
                                : VectorPropertyNumericMetadata.Create(value.GetType()).ElementAtOrDefault(index),
                            () => Array.Empty<PropertyNumericRange?>(), target.Acquire,
                            raw => raw == null ? null : UICoordinate(raw, index));
                        continue;
                    }
                    var submitted = Gui.InputTextWithHint("##Value", mixed ? DifferentValues : "", ref text, 64,
                        ImGuiInputTextFlags.EnterReturnsTrue | (axisCanEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
                    var stepped = axisCanEdit && NativeNumericInput.TryGesture(coordinate.GetType(), null, out _) && NativeNumericInput.StepText(coordinate.GetType(), coordinate, mixed,
                        axisNumericRangeProvider != null ? axisNumericRangeProvider(axis) : VectorPropertyNumericMetadata.Create(value.GetType()).ElementAtOrDefault(axis), ref text);
                    submitted |= stepped;
                    var feedback = Gui.GetID("##Value");
                    if (axisCanEdit && (Gui.IsItemEdited() || stepped)) drafts[draftKey] = text;
                    if (axisCanEdit && (submitted || Gui.IsItemDeactivatedAfterEdit()) && drafts.ContainsKey(draftKey))
                    {
                        try
                        {
                            var replacement = Convert.ChangeType(text, coordinate.GetType(), CultureInfo.InvariantCulture);
                            transform(current => WithUICoordinate(current ?? throw new InvalidOperationException("The property no longer supplies a coordinate target."), index, replacement)); drafts.Remove(draftKey); PropertyConversionFeedback.Validate(feedback);
                        }
                        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); host.ReportValidationFailure(error); }
                    }
                    PropertyConversionFeedback.Draw(feedback);
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }
    private static object UICoordinate(object value, int axis) => value switch
    {
        Int2 item => axis == 0 ? item.X : item.Y,
        Int3 item => axis == 0 ? item.X : axis == 1 ? item.Y : item.Z,
        Int4 item => axis == 0 ? item.X : axis == 1 ? item.Y : axis == 2 ? item.Z : item.W,
        UInt4 item => axis == 0 ? item.X : axis == 1 ? item.Y : axis == 2 ? item.Z : item.W,
        Matrix item => item[axis],
        Rectangle item => axis == 0 ? item.X : axis == 1 ? item.Y : axis == 2 ? item.Width : item.Height,
        RectangleF item => axis == 0 ? item.X : axis == 1 ? item.Y : axis == 2 ? item.Width : item.Height,
        _ => Coordinate(value, axis),
    };
    private static object WithUICoordinate(object value, int axis, object coordinate)
    {
        if (value is Int2 i2) { if (axis == 0) i2.X = (int)coordinate; else i2.Y = (int)coordinate; return i2; }
        if (value is Int3 i3) { if (axis == 0) i3.X = (int)coordinate; else if (axis == 1) i3.Y = (int)coordinate; else i3.Z = (int)coordinate; return i3; }
        if (value is Int4 i4) { if (axis == 0) i4.X = (int)coordinate; else if (axis == 1) i4.Y = (int)coordinate; else if (axis == 2) i4.Z = (int)coordinate; else i4.W = (int)coordinate; return i4; }
        if (value is UInt4 u4) { if (axis == 0) u4.X = (uint)coordinate; else if (axis == 1) u4.Y = (uint)coordinate; else if (axis == 2) u4.Z = (uint)coordinate; else u4.W = (uint)coordinate; return u4; }
        if (value is Matrix matrix) { matrix[axis] = (float)coordinate; return matrix; }
        if (value is Rectangle rectangle) { if (axis == 0) rectangle.X = (int)coordinate; else if (axis == 1) rectangle.Y = (int)coordinate; else if (axis == 2) rectangle.Width = (int)coordinate; else rectangle.Height = (int)coordinate; return rectangle; }
        if (value is RectangleF rectangleF) { var number = MathF.Round((float)coordinate, 3); if (axis == 0) rectangleF.X = number; else if (axis == 1) rectangleF.Y = number; else if (axis == 2) rectangleF.Width = number; else rectangleF.Height = number; return rectangleF; }
        return WithCoordinate(value, axis, (float)coordinate);
    }
    private void DrawUIQuaternion(string identity, object value, IReadOnlyList<object?> values, bool canEdit, Action<Func<object?, object?>> transform, UIValueTarget? valueTarget = null)
    {
        if (!Gui.BeginTable("##Angles", 3, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < 3; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    var rotations = values.Select((other, index) => (Value: other as Quaternion?, State: RotationState(identity, index))).ToArray();
                    var current = RotationState(identity, 0).Read((Quaternion)value)[axis];
                    var mixed = rotations.Any(other => !other.Value.HasValue) || rotations.Skip(1).Any(other => !other.State.Read(other.Value!.Value)[axis].Equals(current));
                    var draftKey = "angle/" + PropertyDraftKey(identity) + "/" + axis;
                    var text = drafts.GetValueOrDefault(draftKey, mixed ? "" : current.ToString("G9", CultureInfo.InvariantCulture));
                    Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine(); Gui.SetNextItemWidth(-float.Epsilon);
                    if (valueTarget is { } target)
                    {
                        var index = axis;
                        host.DrawRetainedNumericProperty(target.Source, target.Identity, typeof(float), current, mixed,
                            target.CanEdit, target.Read, replacement =>
                            {
                                var degrees = (float)replacement!;
                                if (!float.IsFinite(degrees)) throw new ArgumentException("The coordinate must be finite.");
                                var snapshots = rotations.Select(rotation => rotation.State.Capture()).ToArray();
                                var next = 0;
                                try
                                {
                                    target.Transform(original =>
                                    {
                                        var rotation = original is Quaternion authored ? authored
                                            : ObjectFactoryRegistry.NewInstance(typeof(Quaternion)) is Quaternion created ? created
                                            : throw new InvalidOperationException("The nullable rotation could not be created.");
                                        return rotations[next++].State.ReplaceAxis(rotation, index, degrees);
                                    });
                                }
                                catch
                                {
                                    for (var position = 0; position < snapshots.Length; position++) rotations[position].State.Restore(snapshots[position]);
                                    throw;
                                }
                            }, () => null, () => Array.Empty<PropertyNumericRange?>(), target.Acquire,
                            raw => raw is Quaternion rotation ? rotations[0].State.Read(rotation)[index] : null);
                        continue;
                    }
                    var submitted = Gui.InputTextWithHint("##Value", mixed ? DifferentValues : "", ref text, 64,
                        ImGuiInputTextFlags.EnterReturnsTrue | (canEdit ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly));
                    var stepped = canEdit && NativeNumericInput.StepText(typeof(float), current, mixed, null, ref text);
                    submitted |= stepped;
                    var feedback = Gui.GetID("##Value");
                    if (canEdit && (Gui.IsItemEdited() || stepped)) drafts[draftKey] = text;
                    if (canEdit && (submitted || Gui.IsItemDeactivatedAfterEdit()) && drafts.ContainsKey(draftKey))
                    {
                        try
                        {
                            var degrees = PropertyConversionFeedback.Convert(feedback, () => float.Parse(text, CultureInfo.InvariantCulture)); var index = axis;
                            transform(original =>
                            {
                                var rotation = original is Quaternion authored ? authored
                                    : ObjectFactoryRegistry.NewInstance(typeof(Quaternion)) is Quaternion created ? created
                                    : throw new InvalidOperationException("The nullable rotation could not be created.");
                                var state = rotations.FirstOrDefault(item => item.Value == rotation).State ?? new NativeRotationEditorState();
                                return state.ReplaceAxis(rotation, index, degrees);
                            });
                            drafts.Remove(draftKey); PropertyConversionFeedback.Validate(feedback);
                        }
                        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); host.ReportValidationFailure(error); }
                    }
                    PropertyConversionFeedback.Draw(feedback);
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }
}
