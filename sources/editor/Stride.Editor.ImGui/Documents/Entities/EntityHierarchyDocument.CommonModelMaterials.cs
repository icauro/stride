using Stride.Assets.Models;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Core.Quantum;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal void ChangeCommonModelMaterials(IReadOnlyList<ComponentField> fields, IReadOnlyList<IModelAsset> models, int index, Material? material, bool? enabled)
    {
        if (fields.Count < 2 || fields.Count != models.Count || fields.Where((field, position) => !CanEditModelMaterials(field)
            || field.Component.Entity is not { } entity || !IsSelected(entity.Id) || index < 0 || index >= models[position].Materials.Count).Any())
            throw new InvalidOperationException("These material slots cannot be edited together.");
        if (material != null) ContentAssetReference.Validate(typeof(Material), material, allowsNull: true);
        EndEdits();
        if (fields.Any(field => !CanEditModelMaterialSlot(field, index))) throw new InvalidOperationException("A selected material slot is read-only or sealed.");
        var nodes = fields.Select(field => ModelMaterialsNode((ModelComponent)field.Component)).ToArray();
        var key = new NodeIndex(index);
        if (enabled.HasValue && nodes.All(node => ModelMaterialNodeRules.IsEnabled(node, key) == enabled.Value)) return;
        ApplyInheritedGraphMutation(() =>
        {
            for (var position = 0; position < nodes.Length; position++)
            {
                if (!CanEditModelMaterialSlot(fields[position], index)) throw new InvalidOperationException("A selected material slot changed before the edit.");
                var node = nodes[position];
                if (!enabled.HasValue) ModelMaterialNodeRules.Write(node, key, material!);
                else if (ModelMaterialNodeRules.IsEnabled(node, key) != enabled.Value)
                    ModelMaterialNodeRules.SetEnabled(node, key, enabled.Value, models[position]);
            }
        }, "Edit " + nameof(ModelComponent.Materials));
    }
}
