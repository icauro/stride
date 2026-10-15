using Stride.Assets.Models;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanCopyModelMaterial(ComponentField field, int index, IModelAsset model)
        => IsModelMaterials(field) && CanCopyProperty(field) && field.Component.Entity is { } entity && IsSelected(entity.Id)
            && index >= 0 && index < model.Materials.Count;
    internal string CopyModelMaterial(ComponentField field, int index, IModelAsset model)
    {
        EndEdits();
        if (!CanCopyModelMaterial(field, index, model)) throw new InvalidOperationException("The material slot is no longer selected.");
        var node = ModelMaterialsNode((ModelComponent)field.Component);
        return CreatePropertyClipboard([field]).CopyValue(ModelMaterialNodeRules.Read(node, new NodeIndex(index), model), new(node, new NodeIndex(index)));
    }
    internal bool CanPasteModelMaterial(ComponentField field, int index, IModelAsset model, string text)
        => CanCopyModelMaterial(field, index, model) && CanEditModelMaterialSlot(field, index)
            && CreatePropertyClipboard([field]).CanPaste(text, typeof(Material));
    internal void PasteModelMaterial(ComponentField field, int index, IModelAsset model, string text, bool replace)
        => PasteModelMaterials([(field, index, model)], text, replace);
    internal void PasteModelMaterials(IReadOnlyList<(ComponentField Field, int Index, IModelAsset Model)> slots, string text, bool replace)
    {
        EndEdits();
        if (slots.Count == 0 || slots.Any(slot => !CanPasteModelMaterial(slot.Field, slot.Index, slot.Model, text)))
            throw new InvalidOperationException("The clipboard cannot be pasted into these material slots.");
        // Stock slots expose scalar Material values, including actual model defaults.
        // Prepare native conversion/ID/reference fixups before writing their actual
        // IndexingDictionary entries in one inherited journal per canonical owner.
        var changes = slots.DistinctBy(slot => (slot.Field.Node, slot.Index)).Select(slot => (Slot: slot,
            Value: (Material?)CreatePropertyClipboard([slot.Field]).PrepareValue(text, typeof(Material)))).ToArray();
        foreach (var change in changes) if (change.Value != null) ContentAssetReference.Validate(typeof(Material), change.Value, true);
        ApplyInheritedGraphMutation(() =>
        {
            foreach (var change in changes)
            {
                var slot = change.Slot;
                if (!CanPasteModelMaterial(slot.Field, slot.Index, slot.Model, text)) throw new InvalidOperationException("A material slot changed before pasting.");
                ModelMaterialNodeRules.Write(ModelMaterialsNode((ModelComponent)slot.Field.Component), new NodeIndex(slot.Index), change.Value!);
            }
        }, replace ? "Replace property" : "Paste property");
    }
    internal bool CanResetModelMaterial(ComponentField field, int index)
    {
        if (!CanEditModelMaterialSlot(field, index)) return false;
        var node = ModelMaterialsNode((ModelComponent)field.Component); var key = new NodeIndex(index);
        return node.BaseNode != null && (node.Indices ?? []).Contains(key);
    }
    internal void ResetModelMaterial(ComponentField field, int index)
        => ResetModelMaterials([(field, index)]);
    internal void ResetModelMaterials(IReadOnlyList<(ComponentField Field, int Index)> slots)
    {
        EndEdits();
        if (slots.Count == 0 || slots.Any(slot => !CanResetModelMaterial(slot.Field, slot.Index)))
            throw new InvalidOperationException("These material slots have no editable base value.");
        ApplyInheritedGraphMutation(() =>
        {
            foreach (var slot in slots.DistinctBy(slot => (slot.Field.Node, slot.Index)))
            {
                if (!CanResetModelMaterial(slot.Field, slot.Index)) throw new InvalidOperationException("A material slot changed before resetting.");
                ModelMaterialsNode((ModelComponent)slot.Field.Component).ResetOverrideRecursively(new NodeIndex(slot.Index));
            }
        }, "Reset to base value");
    }
}
