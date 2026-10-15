using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Extensions;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static int AggregateFieldIndex(CommonComponentProperty property, ComponentField field)
    {
        for (var index = 0; index < property.Fields.Count; index++) if (ReferenceEquals(property.Fields[index], field)) return index;
        throw new InvalidOperationException("The selected property descriptor changed.");
    }
    private void ApplyAggregateMutation(CommonComponentProperty property, Func<ComponentField, Action> prepare, string title)
    {
        if (sessionHistory == null) throw new InvalidOperationException("The selection has no shared history.");
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).Distinct().ToArray();
        foreach (var owner in owners) owner.EndEdits();
        var changes = property.Fields.Select(field => (Owner: aggregatePropertyOwners[field], Change: prepare(field))).ToArray();
        var leases = new List<IDisposable>();
        try
        {
            foreach (var owner in owners)
            {
                var state = sessionHistory.Registry.Find(owner.Asset.Id);
                if (!ReferenceEquals(state?.Document, owner) || !ReferenceEquals(owner.SessionHistory, sessionHistory))
                    throw new InvalidOperationException("The selected property owner changed.");
                leases.Add(state!.AcquireView());
            }
            using var captured = sessionHistory.CaptureOperationsAtomic(() =>
            {
                foreach (var owner in owners)
                    owner.ApplyAggregateCollectionChanges(changes.Where(change => ReferenceEquals(change.Owner, owner)).Select(change => change.Change).ToArray(), title);
            });
            captured.Publish(sessionHistory, title);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
    private bool CanEditAggregateCollection(CommonComponentProperty property)
        => property.Fields.All(field => aggregatePropertyOwners[field].CanEditAggregateCollection(field));
    private void QueueAggregateMutation(CommonComponentProperty property, Func<ComponentField, Action> prepare, string title)
        => pendingCollectionAction = () => ApplyAggregateMutation(property, prepare, title);

    private void DrawAggregateObject(CommonComponentProperty property)
    {
        Gui.PushID(property.Key); DrawAggregateTopologyLabel(property);
        var values = property.Fields.Select(field => field.Node.Retrieve()).ToArray();
        var entries = NativeAbstractEntries.Combine(property.Fields.Select(field => aggregatePropertyOwners[field].ObjectEntries(field)).ToArray());
        var mixed = NativeAbstractEntryControl.IsMixed(entries, values);
        Gui.BeginDisabled(property.Fields.Any(field => !aggregatePropertyOwners[field].CanEditAggregateObject(field)));
        Gui.SetNextItemWidth(-1);
        if (entries.Count != 0 && Gui.BeginCombo("##Type", mixed ? DifferentPropertyValues : NativeAbstractEntryControl.Label(entries, values[0])))
        {
            NativeAbstractEntryControl.Choices(entries, mixed ? null : values[0], entry =>
                pendingCollectionAction = () => SelectAggregateObjectEntry(property, entry));
            Gui.EndCombo();
        }
        Gui.EndDisabled(); DrawAggregateTopologyReset(property); Gui.PopID();
    }
    private void DrawAggregateModelMaterials(CommonComponentProperty property)
    {
        using var referencePhase = new ContentReferenceCommitPhase(command => pendingCollectionAction = command,
            command => Execute(() => { ValidatePendingPropertyStrings(); command(); }));
        Gui.PushID(property.Key); Gui.SeparatorText(property.First.Label); DrawAggregateTopologyReset(property);
        var layouts = property.Fields.Select(field => ModelMaterialLayout((ModelComponent)field.Component)).ToArray();
        if (layouts.Any(layout => layout == null)) { Gui.PopID(); return; }
        var count = layouts[0]!.Materials.Count;
        for (var index = 0; index < count; index++)
        {
            var name = layouts[0]!.Materials[index].Name;
            var matches = layouts.Select(layout => Enumerable.Range(0, layout!.Materials.Count).Where(slot => layout.Materials[slot].Name == name).ToArray()).ToArray();
            if (matches.Any(candidates => candidates.Length != 1)) continue;
            var slots = matches.Select(indices => indices[0]).ToArray(); Gui.PushID(index);
            Gui.TextUnformatted(name ?? index.ToString(CultureInfo.InvariantCulture)); Gui.SameLine();
            var enabledValues = property.Fields.Select((field, position) => aggregatePropertyOwners[field].IsModelMaterialEnabled(field, slots[position])).ToArray();
            var enabled = enabledValues[0]; var enabledMixed = enabledValues.Skip(1).Any(value => value != enabled);
            var slotEditable = CanEditAggregateCollection(property) && property.Fields.Select((field, position) => aggregatePropertyOwners[field].CanEditAggregateMaterialSlot(field, slots[position])).All(editable => editable);
            Gui.BeginDisabled(!slotEditable);
            Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, enabledMixed);
            if (Gui.Checkbox("##Enabled", ref enabled)) Change(null, enabled);
            Gui.PopItemFlag(); Gui.EndDisabled();
            var materials = property.Fields.Select((field, position) => aggregatePropertyOwners[field].ReadModelMaterial(field, slots[position], layouts[position]!)).ToArray();
            var materialMixed = materials.Skip(1).Any(value => !ContentAssetReference.AreEqual(value, materials[0]));
            Gui.SetNextItemWidth(-1);
            ReferencePicker.DrawInspector(typeof(Material), materials[0], false, value => Change((Material?)value, null),
                forceAssignment: enabledMixed || !enabled, highlighted: name != null && IsSceneMaterialSlotHighlighted(name), mixed: materialMixed, canEdit: slotEditable);
            DrawModelMaterialContext(property.Fields.Select((field, position) => new ModelMaterialClipboardTarget(aggregatePropertyOwners[field], field, slots[position], layouts[position]!)).ToArray(), materials); Gui.PopID();
            void Change(Material? material, bool? state) => referencePhase.Submit(() => ApplyAggregateMutation(property, field =>
            {
                var position = AggregateFieldIndex(property, field);
                return aggregatePropertyOwners[field].PrepareAggregateMaterialChange(field, slots[position], layouts[position]!, material, state);
            }, "Edit " + property.First.Label));
        }
        Gui.PopID();
    }
    private void DrawAggregateCollection(CommonComponentProperty property)
    {
        var contexts = property.Fields.Select(field => aggregatePropertyOwners[field].GetRootHierarchyCollection(field)).ToArray();
        if (contexts.All(context => context != null)) DrawAggregateHierarchyCollection(property, contexts.Select(context => context!).ToArray());
        else { Gui.PushID(property.Key); DrawAggregateTopologyLabel(property); Gui.PopID(); }
    }
    private void AggregateCollectionText(string key, object? value, bool mixed, Action<string> commit, bool enabled = true,
        Type? numericType = null, Func<PropertyNumericRange?>? numericRange = null,
        Func<IReadOnlyList<PropertyNumericRange?>>? rangedRanges = null, object? rangedOwner = null)
    {
        var draftKey = (key, -1); var text = aggregatePropertyDrafts.GetValueOrDefault(draftKey) ?? (mixed ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        var readonlyString = numericType == typeof(string) && !enabled;
        if (readonlyString) { aggregatePropertyDrafts.Remove(draftKey); text = mixed ? "" : (string?)value ?? ""; }
        Gui.BeginDisabled(!enabled); Gui.SetNextItemWidth(-1);
        var submitted = false;
        if (rangedRanges != null && numericType != null && rangedOwner != null)
        {
            submitted = NativeRangedNumericControl.Draw(rangedOwner, numericType, value, mixed, rangedRanges(), UiScale(1), out var display, out _);
            if (display != null && (mixed || !Equals(display, value))) text = Convert.ToString(display, CultureInfo.InvariantCulture) ?? "";
        }
        submitted |= NativeStringInput.DrawWithHint("##Value", mixed ? DifferentPropertyValues : "", ref text, ImGuiInputTextFlags.EnterReturnsTrue | (readonlyString ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None));
        if (numericType != null && NativeNumericInput.TryGesture(numericType, null, out _))
            submitted |= NativeNumericInput.StepText(numericType, value, mixed, numericRange?.Invoke(), ref text);
        var active = Gui.IsItemActive(); var ended = Gui.IsItemDeactivatedAfterEdit();
        if (enabled && active) aggregatePropertyDrafts[draftKey] = text; else aggregatePropertyDrafts.Remove(draftKey);
        var feedback = Gui.GetID("##Value");
        if (enabled && (submitted || ended))
        {
            var priorAction = pendingCollectionAction;
            PropertyConversionFeedback.Run(feedback, () => commit(text));
            if (pendingCollectionAction != priorAction && pendingCollectionAction is { } mutation)
                pendingCollectionAction = () => PropertyConversionFeedback.Run(feedback, mutation);
        }
        PropertyConversionFeedback.Draw(feedback);
        Gui.EndDisabled();
    }
}

