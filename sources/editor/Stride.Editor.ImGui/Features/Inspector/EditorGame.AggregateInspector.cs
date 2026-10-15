using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Compositing;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record AggregateCategory(ComponentPropertySection Presentation, IReadOnlyList<CommonComponentProperty> Properties);
    private sealed record AggregatePanel(string Key, string Label, IReadOnlyList<AggregateCategory> Categories);
    private IReadOnlyList<CommonComponentProperty>? presentedAggregateProperties;
    private IReadOnlyList<AggregatePanel> aggregatePanels = [];
    private void DrawAggregateInspector()
    {
        var properties = ReadAggregateProperties();
        if (!ReferenceEquals(presentedAggregateProperties, properties))
        {
            presentedAggregateProperties = properties;
            aggregatePanels = properties.GroupBy(property => property.Key[..property.Key.IndexOf('/')]).Select(panel =>
            {
                var entries = panel.ToArray(); var byField = entries.ToDictionary(property => property.First);
                var sections = ComponentPropertyPresentation.GetSections(entries.Select(property => property.First).ToArray());
                return new AggregatePanel(panel.Key, entries[0].ComponentLabel, sections.Select(section =>
                    new AggregateCategory(section, section.Fields.Select(field => byField[field]).ToArray())).ToArray());
            }).ToArray();
        }
        foreach (var panel in aggregatePanels)
        {
            Gui.PushID(panel.Key);
            if (Gui.CollapsingHeader(panel.Label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawPresentedCombinedComponentRows(panel.Categories.SelectMany(category => category.Properties).ToArray(), field => aggregatePropertyOwners[field], DrawAggregateProperty);
            }
            Gui.PopID();
        }
        DrawAdditionalCombinedVirtualPanels(SelectedHierarchyDocuments, aggregatePanels.Select(panel => panel.Key));
        ApplyPendingCollectionAction();
    }
    private void DrawAggregateProperty(CommonComponentProperty property)
    {
        if (property.Fields.Any(field => field.Node.Retrieve() is Stride.Core.Yaml.IUnloadable))
        {
            var unloadable = property.Fields.Select(field => field.Node.Retrieve()).OfType<Stride.Core.Yaml.IUnloadable>()
                .DistinctBy(value => (value.TypeName, value.AssemblyName, value.Error)).ToArray();
            Gui.PushID(property.Key);
            try
            {
                for (var index = 0; index < unloadable.Length; index++)
                {
                    Gui.PushID(index);
                    try { DrawUnloadableProperty(unloadable[index]); } finally { Gui.PopID(); }
                }
            }
            finally { Gui.PopID(); }
            return;
        }
        if (property.Fields.All(IsModelNodeName)) { DrawAggregateModelNodeName(property); return; }
        if (EntityHierarchyDocument.IsModelMaterials(property.First)) { DrawAggregateModelMaterials(property); return; }
        if (property.Fields.Any(member => !member.IsVisible) && property.First.Node.Name != "Enabled") return;
        if (property.First.IsIndexedCollection || property.First.IsDictionary) { DrawAggregateCollection(property); return; }
        if (property.First.SupportsObjectLifecycle) { DrawAggregateObject(property); return; }
        var field = property.First; var type = field.Type; var value = property.Value; var mixed = property.IsMixed;
        uint? conversionFeedback = null;
        if (property.Fields.Any(member => !member.IsVisible) && field.Node.Name != "Enabled") return;
        Gui.PushID(property.Key);
        var bold = PushPackagePropertiesBoldFont(property.Fields.Any(member => member.Node is IAssetMemberNode node && node.IsContentOverridden()));
        DrawDocumentedComponentLabel(field); if (bold) Gui.PopFont(); DrawAggregateReset(property);
        if (property.First.IsColor) { DrawRetainedCombinedColor(property, aggregate: true); Gui.PopID(); return; }
        if (UsesCombinedNumericDraft(property)) { DrawCombinedNumericDrafts(property, aggregate: true); Gui.PopID(); return; }
        if (type == typeof(string)) { DrawAggregateString(property); DrawAggregateReset(property); Gui.PopID(); return; }
        if (field.IsContentReference)
        {
            DrawContentReferenceValue(type, value, property.Fields.All(member => member.AllowsNullObject),
                updated => Apply(_ => updated), mixed, canEdit: CanEditAggregateProperty(property));
            DrawAggregateReset(property); Gui.PopID(); return;
        }
        Gui.BeginDisabled(!CanEditAggregateProperty(property));
        using var nullableRow = NativeNullableRow.Begin("##Nullable", field.IsNullable);
        if (field.IsNullable)
        {
            var present = property.Fields.Any(member => member.Node.Retrieve() != null);
            if (DrawNativeNullablePresence(present, out var create, property.Fields.All(member => member.AllowsNullObject))) Apply(member => create ? member.Node.Retrieve() ?? CreateNullableValue(type) : null);
            nullableRow.ValueColumn();
            if (property.Fields.All(member => member.Node.Retrieve() == null)) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); nullableRow.Dispose(); Gui.EndDisabled(); DrawAggregateReset(property); Gui.PopID(); return; }
        }
        if (type == typeof(string)) DrawAggregateString(property);
        else if (!field.IsNavigationGroup && DrawNativeScalarTemplate(field.Node, type, value, mixed, CanEditAggregateProperty(property), updated => Apply(_ => updated))) { }
        else if (field.IsContentReference)
            DrawContentReferenceValue(type, value, property.Fields.All(member => member.AllowsNullObject), updated => Apply(_ => updated), mixed);
        else if (field.IsSceneReference)
        {
            if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : SceneReferenceLabel(value)))
            {
                if (property.Fields.All(member => member.AllowsNullObject) && Gui.Selectable(EditorLocalization.Widget("None", "Button"), !mixed && value == null)) Apply(_ => null);
                foreach (var candidate in AggregateSceneReferenceCandidates(property))
                    if (Gui.Selectable(SceneReferenceLabel(candidate), !mixed && ReferenceEquals(candidate, value))) Apply(_ => candidate);
                Gui.EndCombo();
            }
        }
        else if (field.IsNavigationGroup) DrawNavigationGroupValue(value, mixed, CanEditAggregateProperty(property), updated => Apply(_ => updated));
        else if (type == typeof(SceneCameraSlotId))
        {
            var choices = AggregateCameraSlots(property); var current = value is SceneCameraSlotId slot ? slot : default;
            if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : current.Id == Guid.Empty ? "None" : choices.FirstOrDefault(choice => choice.Value.Equals(current))?.Label ?? current.Id.ToString()))
            {
                if (Gui.Selectable(EditorLocalization.Widget("None", "Button"), !mixed && current.Id == Guid.Empty)) Apply(_ => default(SceneCameraSlotId));
                foreach (var choice in choices) if (Gui.Selectable(choice.Label, !mixed && choice.Value.Equals(current))) Apply(_ => choice.Value);
                Gui.EndCombo();
            }
        }
        else if (type == typeof(bool))
        {
            var next = value is true; Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
            if (Gui.Checkbox("##Value", ref next)) Apply(_ => next); Gui.PopItemFlag();
        }
        else if (type.IsEnum)
        {
            if (DrawComponentEnumArtwork(field, value, mixed, updated => Apply(_ => updated))) { }
            else
            {
                var open = Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : field.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value?.ToString());
                var capture = Controls.Enabled && type.IsDefined(typeof(FlagsAttribute), false) ? "aggregate/" + property.Key + "/flags" : null;
                if (capture != null) Controls.Record(capture);
                if (open)
                {
                foreach (var option in field.EnumOptions)
                {
                    if (!type.IsDefined(typeof(FlagsAttribute), true)) { if (Gui.Selectable(option.Label, !mixed && Equals(option.Value, value))) Apply(_ => option.Value); continue; }
                    var mask = EnumBits(option.Value); var bits = EnumBits(value ?? Enum.ToObject(type, 0));
                    var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                    var different = property.Fields.Skip(1).Any(member =>
                    { var actual = EnumBits(member.Node.Retrieve() ?? Enum.ToObject(type, 0)); return (mask == 0 ? actual == 0 : (actual & mask) == mask) != selected; });
                    Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, different);
                    if (Gui.Checkbox(option.Label, ref selected)) Apply(member =>
                    { var actual = EnumBits(member.Node.Retrieve() ?? Enum.ToObject(type, 0)); return EnumFromBits(type, mask == 0 ? 0 : selected ? actual | mask : actual & ~mask); });
                    if (capture != null) Controls.Record(capture + "/bit/" + option.Name);
                    Gui.PopItemFlag();
                }
                if (type.IsDefined(typeof(FlagsAttribute), true)
                    && (capture == null ? DrawFlagBulkActions() : DrawCapturedComponentFlagActions(capture)) is { } action)
                    Apply(member => ApplyFlagBulkAction((Enum)(member.Node.Retrieve() ?? Enum.ToObject(type, 0)), action));
                Gui.EndCombo();
                }
            }
        }
        else if (field.IsColor) DrawInheritedCommonColor(property, create => Apply(create), () => DrawAggregateReset(property));
        else if (type == typeof(Quaternion))
        {
            var rotationView = Gui.GetID("##Rotation");
            for (var axis = 0; axis < 3; axis++)
            {
                var index = axis; Gui.PushID(axis); Gui.TextUnformatted("XYZ"[axis].ToString()); Gui.SameLine();
                var coordinate = RotationCoordinate(field, rotationView, axis); var different = property.Fields.Skip(1).Any(member => RotationCoordinate(member, rotationView, axis) != coordinate);
                Text(axis, coordinate, different, text => Apply(member => ReplaceRotationCoordinate(member, rotationView, index,
                    float.Parse(text, CultureInfo.InvariantCulture)))); Gui.PopID();
            }
        }
        else if (CommonValueDimensions(type) is var dimensions && dimensions > 0)
        {
            var matrix = UsesExtendedMathTable(type) && Gui.BeginTable("##Matrix", 4, ImGuiTableFlags.SizingStretchSame);
            for (var axis = 0; axis < dimensions && (!UsesExtendedMathTable(type) || matrix); axis++)
            {
                var index = axis; Gui.PushID(axis); DrawExtendedMathCoordinateLabel(type, axis, matrix);
                var coordinate = CommonCoordinate(value, axis); var different = property.Fields.Skip(1).Any(member => !Equals(CommonCoordinate(member.Node.Retrieve(), axis), coordinate));
                Text(axis, coordinate, different, text => Apply(member => WithCommonCoordinate(member.Node.Retrieve(), type, index,
                    type == typeof(UInt4) ? uint.Parse(text, CultureInfo.InvariantCulture) : field.IsIntegerVector || type == typeof(Rectangle) ? (object)int.Parse(text, CultureInfo.InvariantCulture) : float.Parse(text, CultureInfo.InvariantCulture))));
                Gui.PopID();
            }
            if (matrix) Gui.EndTable();
        }
        else if (type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory)) DrawAggregatePropertyPath(property);
        else if (type.IsPrimitive || type == typeof(decimal) || type == typeof(string))
            Text(-1, value, mixed, text => Apply(_ => type == typeof(string) ? text : type == typeof(decimal)
                ? decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture) : Convert.ChangeType(text, type, CultureInfo.InvariantCulture)));
        else Gui.TextUnformatted(mixed ? DifferentPropertyValues : value?.ToString() ?? "None");
        nullableRow.Dispose(); Gui.EndDisabled(); DrawAggregateReset(property); Gui.PopID();
        void Apply(Func<ComponentField, object?> create)
        {
            var feedback = conversionFeedback;
            Execute(() => ApplyAggregateProperty(property, member => feedback is { } id
                ? PropertyConversionFeedback.Convert(id, () => create(member)) : create(member)));
        }
        void Text(int axis, object? coordinate, bool different, Action<string> commit)
        {
            using var axisDisabled = ImGuiScopes.Disabled(axis >= 0 && property.Fields.Any(field => field.IsAxisReadOnly(axis)));
            var key = (property.Key, axis); var text = aggregatePropertyDrafts.GetValueOrDefault(key) ?? (different ? "" : Convert.ToString(coordinate, CultureInfo.InvariantCulture) ?? "");
            var editable = CanEditAggregateProperty(property);
            var readonlyString = type == typeof(string) && !editable;
            if (readonlyString) { aggregatePropertyDrafts.Remove(key); text = different ? "" : (string?)coordinate ?? ""; }
            Gui.SetNextItemWidth(-1);
            var submitted = DrawRangedPropertyText(property.Fields, axis, coordinate, different, ref text);
            submitted |= NativeStringInput.DrawWithHint("##Value", different ? DifferentPropertyValues : "", ref text, ImGuiInputTextFlags.EnterReturnsTrue | (readonlyString ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None));
            submitted |= StepPropertyText(property.Fields, axis, coordinate, different, ref text);
            var feedback = Gui.GetID("##Value");
            var active = Gui.IsItemActive(); var ended = Gui.IsItemDeactivatedAfterEdit();
            if (editable && active) aggregatePropertyDrafts[key] = text; else aggregatePropertyDrafts.Remove(key);
            if (editable && (submitted || ended))
            {
                conversionFeedback = feedback;
                try { Execute(() => PropertyConversionFeedback.Run(feedback, () => commit(text))); }
                finally { conversionFeedback = null; }
            }
            PropertyConversionFeedback.Draw(feedback); DrawAggregateReset(property);
        }
    }
    private IEnumerable<object> AggregateSceneReferenceCandidates(CommonComponentProperty property)
    {
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).Distinct().ToArray();
        foreach (var part in owners[0].Asset.Hierarchy.Parts.Values)
        {
            IEnumerable<object> values = SceneObjectReference.AcceptsEntity(property.First.Type, part.Entity) ? [part.Entity] : part.Entity.Components.Where(property.First.Type.IsInstanceOfType);
            foreach (var value in values)
                if (owners.All(owner => owner.Asset.Hierarchy.Parts.TryGetValue(part.Entity.Id, out var candidate) && ReferenceEquals(candidate.Entity, part.Entity))) yield return value;
        }
    }
    private IReadOnlyList<CameraSlotChoice> AggregateCameraSlots(CommonComponentProperty property)
    {
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).Distinct().ToArray();
        var choices = owners.Select(owner => workspace!.AssetItems.FirstOrDefault(item => item.Id == owner.Asset.Id)?.Package)
            .Select(package => package == null ? [] : CameraSlotChoices.Get(package).ToArray()).ToArray();
        return choices.Length == 0 ? [] : choices[0].Where(choice => choices.Skip(1).All(set => set.Any(other => other.Value.Equals(choice.Value)))).ToArray();
    }
    private void DrawAggregateReset(CommonComponentProperty property)
    {
        if (property.First.IsContentReference || property.First.IsSceneReference) { DrawAggregateTopologyReset(property); return; }
        if (!Gui.BeginPopupContextItem("ResetAggregate")) return;
        if (property.First.Type.IsEnum && property.First.Type.IsDefined(typeof(FlagsAttribute), true))
        {
            using var flagsDisabled = ImGuiScopes.Disabled(!CanEditAggregateProperty(property));
            if (DrawFlagBulkActions() is { } action)
                Execute(() => ApplyAggregateProperty(property, member =>
                    ApplyFlagBulkAction((Enum)(member.Node.Retrieve() ?? Enum.ToObject(member.Type, 0)), action)));
        }
        DrawAggregatePropertyClipboardCommands(property);
        var groups = property.Fields.GroupBy(field => aggregatePropertyOwners[field]).Where(group => group.Any(field => field.Node is IAssetMemberNode { BaseNode: not null })).ToArray();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, groups.Length != 0 && groups.All(group => group.Key.CanResetInheritedScalarBatch(group.ToArray()))))
            Execute(() =>
            {
                ValidatePendingPropertyStrings();
                foreach (var group in groups) group.Key.EndEdits();
                using var prepared = sessionHistory!.CaptureOperationsAtomic(() => { foreach (var group in groups) group.Key.ResetInheritedScalars(group.ToArray()); });
                prepared.Publish(sessionHistory, "Reset " + property.First.Label); aggregatePropertyDrafts.Clear(); commonInheritedColorDrafts.Clear(); commonColorHexDrafts.Clear();
            });
        Gui.EndPopup();
    }
}
