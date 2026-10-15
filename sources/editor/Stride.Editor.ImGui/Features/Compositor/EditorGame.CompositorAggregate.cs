using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Rendering.Compositing;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly string[] CompositorRectangleCoordinates = ["X", "Y", "W", "H"];
    private void DrawCompositorAggregateProperties(AssetMetadataDocument document, CompositorEditorState state)
    {
        var roots = new List<(IObjectNode Node, bool Entry)>();
        foreach (var block in document.CompositorBlocks) if (state.SelectedBlocks.Contains(block.Id)) roots.Add((block.Node, block.Id == Guid.Empty));
        foreach (var item in document.CompositorItems) if (state.SelectedItems.Contains(item.ItemId)) roots.Add((item.Node, false));
        document.SetCompositorPresentationRoots(roots);
        DrawUnloadableProperties(roots.Select(root => root.Node.Retrieve()).ToArray());
        if (state.AggregateRevision != document.EditRevision || state.AggregateAssemblyVersion != CompositorTypeCatalog.Version
            || state.AggregateExtensionVersion != document.SessionHistory?.Extensions?.Version
            || state.AggregatePresentationVersion != document.CompositorPresentationVersion || !IsCompositorAggregateSelectionCurrent(state))
        {
            state.AggregateProperties = document.GetCompositorCommonProperties(roots);
            state.AggregateBlocks = state.SelectedBlocks.ToArray(); state.AggregateItems = state.SelectedItems.ToArray(); state.AggregateRevision = document.EditRevision;
            state.AggregateAssemblyVersion = CompositorTypeCatalog.Version;
            state.AggregateExtensionVersion = document.SessionHistory?.Extensions?.Version;
            state.AggregatePresentationVersion = document.CompositorPresentationVersion;
            state.AggregateDrafts.Clear(); state.AggregateDrag = null;
        }
        DrawCompositorAggregatePropertyGroups(document, state, state.AggregateProperties, roots.Select(root => root.Node).ToArray());
    }
    private static bool IsCompositorAggregateSelectionCurrent(CompositorEditorState state)
    {
        if (state.AggregateBlocks.Length != state.SelectedBlocks.Count || state.AggregateItems.Length != state.SelectedItems.Count) return false;
        foreach (var id in state.AggregateBlocks) if (!state.SelectedBlocks.Contains(id)) return false;
        foreach (var id in state.AggregateItems) if (!state.SelectedItems.Contains(id)) return false;
        return true;
    }
    private static object? CompositorCoordinate(object? value, int axis) => value switch
    {
        Color color => axis == 0 ? color.R : axis == 1 ? color.G : axis == 2 ? color.B : color.A,
        Rectangle rectangle => axis == 0 ? rectangle.X : axis == 1 ? rectangle.Y : axis == 2 ? rectangle.Width : rectangle.Height,
        RectangleF rectangle => axis == 0 ? rectangle.X : axis == 1 ? rectangle.Y : axis == 2 ? rectangle.Width : rectangle.Height,
        _ => CommonCoordinate(value, axis),
    };
    private static object WithCompositorCoordinate(object? value, Type type, int axis, object coordinate)
    {
        if (value is Color color)
        { if (axis == 0) color.R = (byte)coordinate; else if (axis == 1) color.G = (byte)coordinate; else if (axis == 2) color.B = (byte)coordinate; else color.A = (byte)coordinate; return color; }
        if (value is Rectangle rectangle)
        { if (axis == 0) rectangle.X = (int)coordinate; else if (axis == 1) rectangle.Y = (int)coordinate; else if (axis == 2) rectangle.Width = (int)coordinate; else rectangle.Height = (int)coordinate; return rectangle; }
        if (value is RectangleF rectangleF)
        { if (axis == 0) rectangleF.X = (float)coordinate; else if (axis == 1) rectangleF.Y = (float)coordinate; else if (axis == 2) rectangleF.Width = (float)coordinate; else rectangleF.Height = (float)coordinate; return rectangleF; }
        return WithCommonCoordinate(value, type, axis, coordinate);
    }
    private void DrawCompositorAggregateProperty(AssetMetadataDocument document, CompositorEditorState state, CompositorAggregateProperty property)
    {
        if (property.Fields.Any(field => document.CompositorPropertyValue(field) is Stride.Core.Yaml.IUnloadable))
        {
            Gui.PushID(property.Key);
            try
            {
                if (!DrawNativeCompositorValueFactory(document, property.Fields, property)) DrawCompositorLabel(property);
                DrawUnloadableProperties(property.Fields.Select(document.CompositorPropertyValue).ToArray());
                HandleCompositorClipboardKeys(document, property.Fields);
                if (!property.First.Accessor.IsItem && Gui.BeginPopupContextItem("##Property actions"))
                {
                    try
                    {
                        DrawCompositorClipboardActions(document, property.Fields);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), string.Empty, false, document.CanResetCompositorAggregate(property)))
                            ExecuteCompositorCommand(document, () => document.ResetCompositorAggregate(property));
                    }
                    finally { Gui.EndPopup(); }
                }
            }
            finally { Gui.PopID(); }
            return;
        }
        if (property.First.Collection != null) { DrawCompositorAggregateCollection(document, state, property); return; }
        if (!property.First.IsNullable && document.CommonCompositorValueNativeEntries(property.Fields) != null)
        {
            Gui.PushID(property.Key);
            DrawNativeCompositorValueFactory(document, property.Fields, property);
            HandleCompositorClipboardKeys(document, property.Fields);
            if (!property.First.Accessor.IsItem && Gui.BeginPopupContextItem("##Property actions"))
            {
                DrawCompositorClipboardActions(document, property.Fields);
                if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), string.Empty, false, document.CanResetCompositorAggregate(property)))
                    ExecuteCompositorCommand(document, () => document.ResetCompositorAggregate(property));
                Gui.EndPopup();
            }
            Gui.PopID(); return;
        }
        if (!ContentAssetReference.Supports(property.First.Type) && !AssetMetadataDocument.IsCompositorReferenceType(property.First.Type, property.Value)
            && (property.Children.Count > 0 || HasCompositorNativeChildren(property.First) || property.First.Types.Count > 0 || property.Value == null && !property.First.Type.IsValueType && property.First.Type != typeof(string)
                && ObjectFactoryRegistry.CanCreateInstance(property.First.Type)))
        { DrawCompositorAggregateObject(document, state, property); return; }
        Gui.PushID(property.Key); DrawCompositorLabel(property); ShowCompositorDocumentation(property);
        Gui.BeginDisabled(!document.CanEditCompositorAggregate(property) && property.First.Type != typeof(string)
            && !AssetSettingNumericMetadata.Supports(property.First.Type) && !UsesCompositorRetainedMath(property.First.Type) && !UsesCompositorRetainedColor(property.First.Type) && !UsesCompositorRetainedSpecial(property.First.Type) && !UsesCompositorRetainedTemporal(property.First.Type) && !IsPathType(property.First.Type));
        var first = property.First; var type = first.Type; var value = property.Value; var mixed = property.IsMixed;
        if (first.IsNullable)
        {
            using (ImGuiScopes.Disabled(!document.CanEditCompositorAggregate(property)))
                if (Gui.SmallButton(value == null ? "+" : "−")) Apply(field => value == null ? field.Accessor.RetrieveValue() ?? Activator.CreateInstance(type) : null);
            if (property.Fields.Any(field => field.Accessor.RetrieveValue() == null))
            {
                Gui.SameLine(); Gui.TextUnformatted(mixed ? DifferentPropertyValues : "(null)"); Gui.EndDisabled();
                if (!first.Accessor.IsItem && Gui.BeginPopupContextItem("##Nullable property actions"))
                { DrawCompositorClipboardActions(document, property.Fields); Gui.EndPopup(); }
                Gui.PopID(); return;
            }
        }
        if (ContentAssetReference.Supports(type)) DrawContentReferenceValue(type, value, property.Fields.All(field => field.AllowsNull), chosen => Apply(_ => chosen), mixed);
        else if (first.IsNavigationGroup) DrawNavigationGroupValue(value, mixed, document.CanEditCompositorAggregate(property), chosen => Apply(_ => chosen));
        else if (AssetMetadataDocument.IsCompositorReferenceType(type, value))
        {
            if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : CompositorReferenceName(document, value)))
            {
                if (property.Fields.All(field => field.AllowsNull) && Gui.Selectable(EditorLocalization.Widget("None##Null", "Button"), !mixed && value == null)) Apply(_ => null);
                if (type == typeof(SceneCameraSlotId))
                {
                    if (Gui.Selectable(EditorLocalization.Widget("None##EmptyCameraSlot", "Button"), !mixed && value is SceneCameraSlotId { IsEmpty: true })) Apply(_ => default(SceneCameraSlotId));
                    foreach (var choice in document.GetCompositorCameraSlotChoices())
                        if (Gui.Selectable(choice.Label + "##" + choice.Value.Id, !mixed && Equals(value, choice.Value)))
                        { var selected = choice.Value; Apply(_ => selected); }
                }
                else foreach (var candidate in document.CompositorPropertyReferenceItems(first))
                    if (property.Fields.All(field => document.CompositorPropertyReferenceItems(field).Any(item => item.GetType().IsValueType ? Equals(item, candidate) : ReferenceEquals(item, candidate)))
                        && Gui.Selectable(CompositorReferenceName(document, candidate) + "##" + CompositorReferenceIdentity(candidate), !mixed && (candidate.GetType().IsValueType ? Equals(value, candidate) : ReferenceEquals(value, candidate)))) { var selected = candidate; Apply(_ => selected); }
                foreach (var candidate in first.Types)
                    if (property.Fields.All(field => field.Types.Contains(candidate)) && Gui.Selectable(Stride.Core.DisplayAttribute.GetDisplayName(candidate)))
                    { var chosen = candidate; UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => document.CreateCompositorAggregateObjects(property, chosen)); }
                Gui.EndCombo();
            }
        }
        else if (type == typeof(bool))
        {
            var selected = value is true; Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
            if (Gui.Checkbox("##Value", ref selected)) Apply(_ => selected); Gui.PopItemFlag();
        }
        else if (type.IsEnum && icons!.HasEnumImages(type)) DrawCompositorAggregateEnumArtwork(document, property);
        else if (type.IsEnum)
        {
            if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : value?.ToString()))
            {
                for (var index = 0; index < first.EnumNames.Length; index++)
                {
                    var option = Enum.Parse(type, first.EnumNames[index]);
                    if (!type.IsDefined(typeof(FlagsAttribute), true)) { if (Gui.Selectable(first.EnumLabels[index], !mixed && Equals(value, option))) Apply(_ => option); continue; }
                    var mask = EnumBits(option); var bits = EnumBits(value!); var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                    var different = property.Fields.Any(field => { var actual = EnumBits(field.Accessor.RetrieveValue()!); return (mask == 0 ? actual == 0 : (actual & mask) == mask) != selected; });
                    Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, different);
                    if (Gui.Checkbox(first.EnumLabels[index], ref selected)) Apply(field => { var actual = EnumBits(field.Accessor.RetrieveValue()!); return EnumFromBits(type, mask == 0 ? 0 : selected ? actual | mask : actual & ~mask); });
                    Gui.PopItemFlag();
                }
                if (type.IsDefined(typeof(FlagsAttribute), true) && DrawFlagBulkActions() is { } bulk)
                    Apply(field => ApplyFlagBulkAction((Enum)field.Accessor.RetrieveValue()!, bulk));
                Gui.EndCombo();
            }
        }
        else if (UsesCompositorRetainedSpecial(type)) DrawCompositorRetainedSpecial(document, property.Fields, property);
        else if (UsesCompositorRetainedMath(type)) DrawCompositorRetainedMath(document, property.Fields, property);
        else if (UsesCompositorRetainedColor(type)) DrawCompositorRetainedColor(document, property.Fields, property);
        else if (UsesCompositorRetainedTemporal(type)) DrawCompositorRetainedTemporal(document, property.Fields, property);
        else if (IsPathType(type)) DrawCompositorRetainedPath(document, property.Fields, property);
        else if (AssetSettingNumericMetadata.Supports(type)) DrawCompositorRetainedNumeric(document, property.Fields, property);
        else if (type == typeof(string)) DrawCompositorRetainedString(document, property.Fields, property);
        else if (type.IsPrimitive || type == typeof(decimal) || type == typeof(DateTimeOffset))
            Text(-1, value, mixed, text => Apply(_ => type == typeof(DateTimeOffset) ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)
                : type == typeof(decimal) ? decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)
                : type == typeof(char) && text.Length == 0 ? '\0' : Convert.ChangeType(text, type, CultureInfo.InvariantCulture)));
        else DrawSelectableText("##Value", mixed ? DifferentPropertyValues : value?.ToString() ?? "None", true, new System.Numerics.Vector2(0, Gui.GetTextLineHeight()));
        Gui.EndDisabled();
        HandleCompositorClipboardKeys(document, property.Fields);
        if (!first.Accessor.IsItem && Gui.BeginPopupContextItem("##Reset aggregate"))
        {
            DrawCompositorClipboardActions(document, property.Fields);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), string.Empty, false, document.CanResetCompositorAggregate(property)))
            { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => document.ResetCompositorAggregate(property)); }
            Gui.EndPopup();
        }
        Gui.PopID();
        void Apply(Func<CompositorProperty, object?> create) { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => document.EditCompositorAggregate(property, create)); }
        void Text(int axis, object? coordinate, bool different, Action<string> commit)
        {
            var key = (property.Key, axis); var text = state.AggregateDrafts.GetValueOrDefault(key)
                ?? (different ? "" : coordinate is DateTime date ? date.ToString("O", CultureInfo.InvariantCulture) : coordinate is DateTimeOffset offset ? offset.ToString("O", CultureInfo.InvariantCulture) : Convert.ToString(coordinate, CultureInfo.InvariantCulture) ?? "");
            Gui.SetNextItemWidth(-1);
            var submitted = Gui.InputTextWithHint("##Value", different ? DifferentPropertyValues : "", ref text, 32768, ImGuiInputTextFlags.EnterReturnsTrue);
            var feedback = Gui.GetID("##Value");
            var stepped = false;
            if (coordinate != null && AssetSettingNumericMetadata.Supports(coordinate.GetType()) && Gui.IsItemActive()
                && (Gui.IsKeyPressed(ImGuiKey.UpArrow) || Gui.IsKeyPressed(ImGuiKey.DownArrow)
                    || Gui.IsKeyPressed(ImGuiKey.PageUp) || Gui.IsKeyPressed(ImGuiKey.PageDown)))
            {
                var numericType = coordinate.GetType();
                var range = CompositorAggregateNumericRange(property, axis, numericType, out var smallStep, out var largeStep);
                if (TryCompositorNumericGesture(numericType, range, out var amount, smallStep, largeStep))
                    ExecuteCompositorCommand(document, () => PropertyConversionFeedback.Run(feedback, () =>
                    {
                        // The original mixed NumericTextBox uses its range endpoint,
                        // rather than independently incrementing each selected value.
                        var baseline = string.IsNullOrWhiteSpace(text) ? null : Convert.ChangeType(text, numericType, CultureInfo.InvariantCulture);
                        text = Convert.ToString(StepCompositorNumber(numericType, baseline, range, amount), CultureInfo.InvariantCulture) ?? "";
                        commit(text); stepped = true;
                    }));
            }
            if (Gui.IsItemActive()) state.AggregateDrafts[key] = text; else state.AggregateDrafts.Remove(key);
            if (!stepped && (submitted || Gui.IsItemDeactivatedAfterEdit())) ExecuteCompositorCommand(document, () => PropertyConversionFeedback.Run(feedback, () => commit(text)));
            PropertyConversionFeedback.Draw(feedback);
        }
    }

    private static PropertyNumericRange CompositorAggregateNumericRange(CompositorAggregateProperty property, int axis, Type type,
        out double smallStep, out double largeStep)
    {
        PropertyNumericRange? Read(CompositorProperty field) => axis < 0 ? field.Range
            : axis < field.VectorRanges.Count ? field.VectorRanges[axis] : null;
        double? Common(Func<PropertyNumericRange, double?> select)
        {
            var first = Read(property.First); var value = first == null ? null : select(first);
            foreach (var field in property.Fields)
            { var range = Read(field); if (value != (range == null ? null : select(range))) return null; }
            return value;
        }
        var decimalPlaces = Read(property.First)?.DecimalPlaces;
        foreach (var field in property.Fields) if (Read(field)?.DecimalPlaces != decimalPlaces) { decimalPlaces = null; break; }
        smallStep = Common(range => range.DeclaredSmallStep) ?? 1;
        largeStep = Common(range => range.DeclaredLargeStep) ?? (type == typeof(byte) || type == typeof(sbyte) ? 5 : 10);
        // Attached metadata is independent and may have a common small step
        // greater than a differing large step's fallback.
        return new(new PropertyNumericMetadata(
            Common(range => range.Minimum) ?? Convert.ToDouble(Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters.NumericValueNodeUpdater.GetMinimum(type), CultureInfo.InvariantCulture),
            Common(range => range.Maximum) ?? Convert.ToDouble(Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters.NumericValueNodeUpdater.GetMaximum(type), CultureInfo.InvariantCulture),
            smallStep, largeStep, decimalPlaces), type, decimalPlaces != null);
    }
}
