#if NALA
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Engine.Processors;
using Stride.Graphics;
using Stride.Graphics.Data;

sealed partial class ComputeSkinningSample
{
    private void VerifyNalaGpuOutput()
    {
        for (int instance = 0; instance < InstanceCount; instance++)
        for (int meshIndex = 0; meshIndex < sharedModel.Meshes.Count; meshIndex++)
        {
            var mesh = sharedModel.Meshes[meshIndex];
            var definition = mesh.BlendShapes;
            var info = models[instance].MeshInfos[meshIndex];
            if (!info.GpuBlendShapeInitialized || info.UseFusedSkinning || info.ClonedMeshDraw == null || info.GpuSparseInitialized != (mode == "sparse"))
                throw new Exception($"Nala selected GPU morph path is not active on instance {instance}.");
            var binding = mesh.Draw.VertexBuffers[0];
            var output = info.ClonedMeshDraw.VertexBuffers[0];
            byte[] actual = output.Buffer.GetData<byte>(GraphicsContext.CommandList);
            byte[] source = binding.Buffer.GetSerializationData().Content;
            int Offset(string name) => binding.Declaration.EnumerateWithOffsets().First(x => x.VertexElement.SemanticName == name).Offset;
            int indicesOffset = Offset("BLENDINDICES"), weightsOffset = Offset("BLENDWEIGHT");
            bool indices16 = binding.Declaration.VertexElements.First(x => x.SemanticName == "BLENDINDICES").Format == PixelFormat.R16G16B16A16_UInt;
            var inverse = Matrix.Invert(models[instance].Skeleton.NodeTransformations[mesh.NodeIndex].WorldMatrix);
            Vector3 Read(int address) => MemoryMarshal.Read<Vector3>(actual.AsSpan(address, 12));
            for (int sample = 0; sample < 12; sample++)
            {
                int vertex = sample * (definition.VertexCount - 1) / 11;
                Vector3 p = definition.BasePositions[vertex], n = definition.BaseNormals[vertex], t = definition.BaseTangents[vertex];
                foreach (var target in definition.Targets)
                {
                    float weight = components[instance].GetWeight(target.Name);
                    if (Math.Abs(weight) <= BlendShapeGpuDeformer.WeightEpsilon) continue;
                    if (target.HasDeltaPositions) p += target.DeltaPositions[vertex] * weight;
                    if (target.HasDeltaNormals) n += target.DeltaNormals[vertex] * weight;
                    if (target.HasDeltaTangents) t += target.DeltaTangents[vertex] * weight;
                }
                int address = binding.Offset + vertex * binding.Stride;
                Vector3 sp = p, sn = Vector3.Normalize(n), st = Vector3.Normalize(t);
                int outputAddress = output.Offset + vertex * output.Stride;
                if (Vector3.Distance(sp, Read(outputAddress + definition.PositionOffset)) > 0.0001f ||
                    Vector3.Distance(sn, Read(outputAddress + definition.NormalOffset)) > 0.0001f ||
                    Vector3.Distance(st, Read(outputAddress + definition.TangentOffset)) > 0.0001f)
                    throw new Exception($"Nala GPU/CPU mismatch: instance {instance}, vertex {vertex}, frame {frame}; expected position {sp}, actual {Read(outputAddress + definition.PositionOffset)}.");
            }
        }
        gpuVerified = true;
        Console.WriteLine($"PASS Nala GPU/CPU frame {frame}: {InstanceCount} independent dense/sparse instances, position/normal/tangent.");
    }
}
#endif
