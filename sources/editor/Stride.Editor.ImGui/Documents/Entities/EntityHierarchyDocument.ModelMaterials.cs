using Stride.Assets.Models;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal static bool IsModelReference(ComponentField field) => field.Component is ModelComponent && field.Name == nameof(ModelComponent.Model);
    internal static bool IsModelMaterials(ComponentField field) => field.Component is ModelComponent && field.Name == nameof(ModelComponent.Materials);

    private IAssetObjectNode ModelMaterialsNode(ModelComponent component)
    {
        EnsureHierarchyGraph();
        return (IAssetObjectNode)hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(component)[nameof(ModelComponent.Materials)].Target!;
    }
    private void UpdateModelReferences(IReadOnlyList<ComponentField> fields, object?[] values)
    {
        EndEdits();
        bool Editable(ComponentField field) => IsModelReference(field) && field.SupportsEditing && CanEditHierarchyField(field)
            && AssetMetadataDocument.CanAssignAuthoringNode(field.Node) && field.Presentation?.IsReadOnly(field) != true
            && CanEditAggregateModelReference(field);
        if (fields.Count == 0 || fields.Count != values.Length || fields.Any(field => !Editable(field)))
            throw new InvalidOperationException("These model references cannot be edited.");
        for (var index = 0; index < fields.Count; index++) ContentAssetReference.Validate(fields[index].Type, values[index], fields[index].AllowsNullObject);
        if (fields.Select((field, index) => ContentAssetReference.AreEqual(field.Node.Retrieve(), values[index])).All(equal => equal)) return;
        ApplyInheritedGraphMutation(() =>
        {
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                if (!Editable(field)) throw new InvalidOperationException("A selected model reference became read-only or sealed.");
                if (ContentAssetReference.AreEqual(field.Node.Retrieve(), values[index])) continue;
                ModelMaterialNodeRules.Clear(ModelMaterialsNode((ModelComponent)field.Component));
                field.Node.Update(values[index]);
            }
        }, "Edit " + nameof(ModelComponent.Model));
    }

    internal bool CanEditModelMaterials(ComponentField field) => IsModelMaterials(field) && field.Component.Entity is { } entity
        && IsSelected(entity.Id) && entity.Components.Contains(field.Component) && CanEditHierarchyField(field)
        && field.Presentation?.IsReadOnly(field) != true && !PropertyClipboardRules.IsReadOnly(field.Node)
        && CanEditHierarchyNode(ModelMaterialsNode((ModelComponent)field.Component));

    internal bool CanEditModelMaterialSlot(ComponentField field, int index)
    {
        if (!CanEditModelMaterials(field) || index < 0) return false;
        var node = ModelMaterialsNode((ModelComponent)field.Component); var key = new NodeIndex(index);
        return !HierarchyGraph.IsInheritedNodeSealed(node, key)
            && ((node.Indices ?? []).Contains(key) ? CanEditHierarchyItem(node, key) : PropertyClipboardRules.CanAdd(node));
    }
    internal Material? ReadModelMaterial(ComponentField field, int index, IModelAsset model) =>
        ModelMaterialNodeRules.Read(ModelMaterialsNode((ModelComponent)field.Component), new NodeIndex(index), model);
    internal bool IsModelMaterialEnabled(ComponentField field, int index) =>
        ModelMaterialNodeRules.IsEnabled(ModelMaterialsNode((ModelComponent)field.Component), new NodeIndex(index));

    internal void ChangeModelMaterial(ComponentField field, int index, IModelAsset model, Material? value, bool? enabled)
    {
        EndEdits();
        if (!CanEditModelMaterialSlot(field, index) || index >= model.Materials.Count) throw new InvalidOperationException("This material slot cannot be edited.");
        if (value != null) ContentAssetReference.Validate(typeof(Material), value, allowsNull: true);
        var node = ModelMaterialsNode((ModelComponent)field.Component); var key = new NodeIndex(index);
        if (enabled.HasValue && enabled.Value == ModelMaterialNodeRules.IsEnabled(node, key)) return;
        ApplyInheritedGraphMutation(() =>
        {
            if (!CanEditModelMaterialSlot(field, index)) throw new InvalidOperationException("This material slot changed before the edit.");
            if (enabled.HasValue) ModelMaterialNodeRules.SetEnabled(node, key, enabled.Value, model);
            else ModelMaterialNodeRules.Write(node, key, value!);
        }, "Edit " + nameof(ModelComponent.Materials));
    }
}
