using System.Linq;
using Stride.Assets.Models;
using Stride.Core.Quantum;
using Stride.Rendering;

namespace Stride.Assets.Presentation.NodePresenters.Rules;

internal static class ModelMaterialNodeRules
{
    internal static Material Read(IObjectNode materials, NodeIndex index, IModelAsset model)
    {
        if (materials.Indices.Contains(index)) return (Material)materials.Retrieve(index);
        return model != null && index.Int < model.Materials.Count ? model.Materials[index.Int].MaterialInstance?.Material : null;
    }
    internal static void Write(IObjectNode materials, NodeIndex index, Material value)
    {
        if (materials.Indices.Contains(index)) materials.Update(value, index); else materials.Add(value, index);
    }
    internal static bool IsEnabled(IObjectNode materials, NodeIndex index) => materials.Indices.Contains(index);
    internal static void SetEnabled(IObjectNode materials, NodeIndex index, bool value, IModelAsset model)
    {
        if (value) materials.Add(Read(materials, index, model), index); else materials.Remove(materials.Retrieve(index), index);
    }
    internal static void Clear(IObjectNode materials)
    {
        foreach (var index in materials.Indices.ToArray()) materials.Remove(materials.Retrieve(index), index);
    }
}
