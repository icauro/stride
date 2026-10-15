using Stride.Assets.Materials;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders.Parsing.SDSL.AST;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal Func<IDictionary<string, string>>? MaterialShaderSources { get; set; }
    internal void UpdateMaterialContextNode(IMemberNode member, object? value)
    {
        member.Update(value);
        if (member.Parent.Retrieve() is MaterialAttributes)
        {
            var counterpart = member.Name switch
            {
                nameof(MaterialAttributes.Diffuse) => nameof(MaterialAttributes.DiffuseModel),
                nameof(MaterialAttributes.Specular) => nameof(MaterialAttributes.SpecularModel),
                _ => null
            };
            if (counterpart != null)
            {
                var model = member.Parent[counterpart];
                var current = model.Retrieve();
                if ((value == null && current != null || value != null && current == null) && !CanEditAssetSettingNode(model))
                    throw new InvalidOperationException("The inherited material shading model cannot be edited.");
                if (value == null && current != null) model.Update(null);
                else if (value != null && current == null)
                    model.Update(member.Name == nameof(MaterialAttributes.Diffuse)
                        ? (object)new MaterialDiffuseLambertModelFeature() : new MaterialSpecularMicrofacetModelFeature());
            }
        }
        if (member.Name != nameof(ComputeShaderClassColor.MixinReference) || member.Parent.Retrieve() is not IComputeNode) return;
        var sources = MaterialShaderSources?.Invoke() ?? throw new InvalidOperationException("The owning material package's shader source context is unavailable.");
        if (member.Parent.Retrieve() is ComputeShaderClassColor color) UpdateMaterialShader(color, member.Parent, sources);
        else if (member.Parent.Retrieve() is ComputeShaderClassScalar scalar) UpdateMaterialShader(scalar, member.Parent, sources);
    }
    private void UpdateMaterialShader<T>(ComputeShaderClassBase<T> shaderNode, IObjectNode owner, IDictionary<string, string> sources)
        where T : class, IComputeNode
    {
        var shader = shaderNode.ParseReferencedShader(sources);
        var generics = (IAssetObjectNode)owner[nameof(ComputeShaderClassColor.Generics)].Target!;
        if (!CanEditAssetSettingNode(generics)) throw new InvalidOperationException("The inherited shader generics cannot be edited.");
        var expected = new Dictionary<string, Type>();
        if (shader?.Generics != null)
            foreach (var parameter in shader.Generics.Parameters)
                if (ComputeShaderClassHelper.GetComputeColorParameterType(parameter.TypeName.Name) is { } type) expected[parameter.Name.Name] = type;
        foreach (var index in (generics.Indices ?? []).ToArray())
            if (!expected.TryGetValue((string)index.Value, out var type))
            {
                if (!CanEditAssetSettingItem(generics, index)) throw new InvalidOperationException("The inherited shader generic cannot be edited.");
                generics.Remove(generics.Retrieve(index)!, index);
            }
            else if (!type.IsInstanceOfType(generics.Retrieve(index)))
            {
                if (!CanEditAssetSettingItem(generics, index)) throw new InvalidOperationException("The inherited shader generic cannot be edited.");
                var value = Activator.CreateInstance(type)!; AssetCollectionItemIdHelper.GenerateMissingItemIds(value); generics.Update(value, index);
            }
        foreach (var parameter in expected)
            if (!(generics.Indices ?? []).Contains(new NodeIndex(parameter.Key)))
            {
                var value = Activator.CreateInstance(parameter.Value)!; AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
                generics.Add(value, new NodeIndex(parameter.Key));
            }
        var compositions = (IAssetObjectNode)owner[nameof(ComputeShaderClassColor.CompositionNodes)].Target!;
        if (!CanEditAssetSettingNode(compositions)) throw new InvalidOperationException("The inherited shader compositions cannot be edited.");
        var names = (shader?.Elements.OfType<ShaderMember>().Where(member => member.IsCompose && member.TypeName.Name == "ComputeColor")
            .Select(member => member.Name.Name) ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var index in (compositions.Indices ?? []).ToArray())
            if (!names.Contains((string)index.Value))
            {
                if (!CanEditAssetSettingItem(compositions, index)) throw new InvalidOperationException("The inherited shader composition cannot be edited.");
                compositions.Remove(compositions.Retrieve(index)!, index);
            }
        foreach (var name in names)
            if (!(compositions.Indices ?? []).Contains(new NodeIndex(name))) compositions.Add(null!, new NodeIndex(name));
    }
}
