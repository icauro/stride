using Stride.Rendering;
using Stride.Engine;
using Stride.Core.Serialization;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private IEnumerable<Model> ImportedRuntimeModels(EntityHierarchyDocument? document)
        => componentContent.Values.OfType<Model>().Concat(ImportRuntimeEntities(document)
            .SelectMany(entity => entity.Components.OfType<ModelComponent>()).Select(component => component.Model).Where(model => model != null))
            .Distinct<Model>(ReferenceEqualityComparer.Instance);
    private void PrepareImportedMaterialConsumers(string url, Material material, EntityHierarchyDocument? document,
        List<CompiledTextureConsumers.Change> changes)
    {
        foreach (var model in ImportedRuntimeModels(document))
            foreach (var slot in model.Materials)
                if (slot.Material is { } previous && AttachedReferenceManager.GetUrl(previous) == url)
                    changes.Add(new(() => slot.Material = material, () => slot.Material = previous));
    }
    private bool UsesImportedMaterial(IReadOnlyCollection<object> values, EntityHierarchyDocument? document)
    {
        var owned = new HashSet<object>(values, ReferenceEqualityComparer.Instance);
        return ImportedRuntimeModels(document).Any(model => model.Materials.Any(slot => slot.Material != null && owned.Contains(slot.Material)));
    }
}
