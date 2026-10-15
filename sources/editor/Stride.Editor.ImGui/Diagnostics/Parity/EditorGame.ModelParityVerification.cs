using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed record ModelParityGeometryResult(string Id, string AssetId, int MeshCount, long VertexBytes, int DrawCount, int TextureCoordinateCount);

internal sealed partial class EditorGame
{
    internal ModelParityCaseResult[] RunModelParityHistory(string privateRoot) => ModelParityCases.RunHistory(privateRoot);

    /// <summary>Generates the actual persisted stock primitive through engine services and retires its real GPU buffers.</summary>
    internal ModelParityGeometryResult[] GenerateModelParityMeshes(ModelParityCaseResult[] cases)
    {
        var results = new List<ModelParityGeometryResult>();
        foreach (var test in cases)
        {
            var asset = AssetFileSerializer.Load<ProceduralModelAsset>(test.AssetPath).Asset;
            if (asset.Id.ToString() != test.AssetId) throw new InvalidDataException("A model parity asset changed before native generation.");
            var model = new Model();
            var resources = new ThumbnailGpuResources(this);
            try
            {
                if (asset.Type.GetType().FullName != test.ModelType || asset.Type is not Stride.Rendering.ProceduralModels.PrimitiveProceduralModelBase primitive)
                    throw new InvalidDataException("The persisted parity asset no longer has its expected stock primitive type.");
                primitive.Generate(base.Services, model, buffer => resources.Retain(buffer));
                var bytes = model.Meshes.Sum(mesh => mesh.Draw.VertexBuffers.Sum(binding => (long)binding.Buffer.SizeInBytes));
                var draws = model.Meshes.Sum(mesh => mesh.Draw.DrawCount);
                if (model.Meshes.Count == 0 || bytes <= 0 || draws <= 0)
                    throw new InvalidOperationException("The stock procedural compiler generated no real mesh data: " + test.Id);
                var uvChannels = model.Meshes[0].Draw.VertexBuffers[0].Declaration.VertexElements.Count(element => element.SemanticName == "TEXCOORD");
                if (uvChannels != primitive.NumberOfTextureCoordinates)
                    throw new InvalidOperationException("The native mesh declaration does not reflect the persisted texture-coordinate count: " + test.Id);
                results.Add(new(test.Id, test.AssetId, model.Meshes.Count, bytes, draws, uvChannels));
            }
            finally { resources.Retire(); }
        }
        return results.ToArray();
    }
}
