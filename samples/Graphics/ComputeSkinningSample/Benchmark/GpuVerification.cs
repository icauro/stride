#if !NALA
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Rendering;
using Stride.Engine;

sealed partial class ComputeSkinningSample
{
    private readonly Dictionary<Stride.Graphics.Buffer, byte[]> referenceVertices = new();
    private readonly Stride.Graphics.Buffer[] lastOutputs;
    private Stride.Graphics.Buffer removedOutput;
    private void VerifyGpuOutput()
    {
        var processors = SceneSystem.SceneInstance.Processors.OfType<ModelRenderProcessor>().ToArray();
        for (int instance = 0; instance < InstanceCount; instance++)
        {
            var processor = processors.First(x => x.RenderModels.ContainsKey(models[instance]));
            var rendered = processor.RenderModels[models[instance]];
            for (int meshIndex = 0; meshIndex < sharedModel.Meshes.Count; meshIndex++)
            {
                var mesh = models[instance].Model.Meshes[meshIndex];
                var vb = mesh.Draw.VertexBuffers[0];
                var data = mesh.MorphTargets;
                var renderMesh = rendered.Meshes[rendered.Materials[meshIndex].MeshStartIndex];
                var outputBinding = renderMesh.Mesh.Draw.VertexBuffers[0];
                if (ReferenceEquals(outputBinding.Buffer, vb.Buffer))
                {
                    if (models[instance].SkinningMode != SkinningMode.VertexShader || data != null && models[instance].Morphs.Enabled)
                        throw new Exception("Compute output was not installed on the render mesh.");
                    if (renderMesh.Mesh.Skinning == null || renderMesh.BlendMatrices == null)
                        throw new Exception("Skinning-only VS fallback was not restored.");
                    continue;
                }
                lastOutputs[instance] = outputBinding.Buffer;
                byte[] actual = outputBinding.Buffer.GetData<byte>(GraphicsContext.CommandList);
                if (!referenceVertices.TryGetValue(vb.Buffer, out var source))
                    referenceVertices[vb.Buffer] = source = vb.Buffer.GetSerializationData()?.Content ?? vb.Buffer.GetData<byte>(GraphicsContext.CommandList);
                int Offset(string name) => vb.Declaration.EnumerateWithOffsets().First(x => x.VertexElement.SemanticName == name).Offset;
                Vector3 Read(byte[] bytes, int address) => MemoryMarshal.Read<Vector3>(bytes.AsSpan(address, 12));
                int pOffset = Offset("POSITION"), nOffset = Offset("NORMAL"), tOffset = Offset("TANGENT");
                var bones = mesh.Skinning == null || models[instance].SkinningMode != SkinningMode.Compute ? Array.Empty<Matrix>() : models[instance].MeshInfos[meshIndex].BlendMatrices;
                bool morphEnabled = models[instance].Morphs.Enabled;
                if (mesh.Skinning != null && models[instance].SkinningMode == SkinningMode.VertexShader && (renderMesh.Mesh.Skinning == null || renderMesh.BlendMatrices == null))
                    throw new Exception("Vertex-shader skinning was not retained after morph compute.");
                var inverse = Matrix.Invert(renderMesh.World);
                for (int sample = 0; sample < 12; sample++)
                {
                    int vertex = sample * (vb.Count - 1) / 11;
                    int address = vb.Offset + vertex * vb.Stride;
                    Vector3 p = Read(source, address + pOffset), n = Read(source, address + nOffset), t = Read(source, address + tOffset);
                    for (uint entry = 0; morphEnabled && data != null && entry < (data.Layout == MeshMorphLayout.DenseMorphMajor ? data.TargetNames.Length : data.VertexOffsets[vertex + 1] - data.VertexOffsets[vertex]); entry++)
                    {
                        var delta = data.Entries[data.Layout == MeshMorphLayout.DenseMorphMajor ? entry * data.VertexCount + vertex : data.VertexOffsets[vertex] + entry];
                        float weight = models[instance].GetMorphWeight(meshIndex, delta.ShapeIndex);
                        p += delta.PositionDelta * weight; n += delta.NormalDelta * weight; t += delta.TangentDelta * weight;
                    }
                    if (bones.Length != 0)
                    {
                        int indexOffset = Offset("BLENDINDICES"), weightOffset = Offset("BLENDWEIGHT");
                        bool indices16 = vb.Declaration.VertexElements.First(x => x.SemanticName == "BLENDINDICES").Format == PixelFormat.R16G16B16A16_UInt;
                        var weights = MemoryMarshal.Read<Vector4>(source.AsSpan(address + weightOffset, 16));
                        Vector3 sp = Vector3.Zero, sn = Vector3.Zero, st = Vector3.Zero;
                        for (int bone = 0; bone < 4; bone++)
                        {
                            int index = indices16 ? BitConverter.ToUInt16(source, address + indexOffset + bone * 2) : source[address + indexOffset + bone];
                            if (weights[bone] == 0) continue;
                            var matrix = bones[index] * inverse;
                            sp += Vector3.TransformCoordinate(p, matrix) * weights[bone];
                            sn += Vector3.TransformNormal(n, matrix) * weights[bone];
                            st += Vector3.TransformNormal(t, matrix) * weights[bone];
                        }
                        p = sp; n = sn; t = st;
                    }
                    n.Normalize(); t -= n * Vector3.Dot(n, t); t.Normalize();
                    int outputAddress = outputBinding.Offset + vertex * outputBinding.Stride;
                    float tolerance = 0.0001f;
                    if (Vector3.Distance(p, Read(actual, outputAddress + pOffset)) > tolerance ||
                        Vector3.Distance(n, Read(actual, outputAddress + nOffset)) > tolerance ||
                        Vector3.Distance(t, Read(actual, outputAddress + tOffset)) > tolerance)
                        throw new Exception($"GPU/CPU mismatch: instance {instance}, mesh {meshIndex}, vertex {vertex}, frame {frame}, {mode}. Position expected {p}, actual {Read(actual, outputAddress + pOffset)}; normal expected {n}, actual {Read(actual, outputAddress + nOffset)}; tangent expected {t}, actual {Read(actual, outputAddress + tOffset)}.");
                    // Other imported attributes and tangent handedness must survive bit-for-bit.
                    for (int word = 0; word < vb.Stride; word += 4)
                    {
                        if (word >= pOffset && word < pOffset + 12 || word >= nOffset && word < nOffset + 12 || word >= tOffset && word < tOffset + 12) continue;
                        if (BitConverter.ToUInt32(source, address + word) != BitConverter.ToUInt32(actual, outputAddress + word))
                            throw new Exception("Compute changed an unrelated vertex attribute.");
                    }
                }
            }
        }
        gpuVerified = true;
        Console.WriteLine($"PASS GPU/CPU {mode}, frame {frame}: position, normal, tangent, preserved attributes; {InstanceCount} independent instances.");
    }
    private bool zeroWeightsThisFrame;

    private void ApplyLifecycleChanges()
    {
        if (frame == 12 && !SkinningOnly) zeroWeightsThisFrame = true;
        if (frame == 13) previous[0] = -1;
        if (frame == 16) models[0].SkinningMode = SkinningMode.VertexShader;
        if (frame == 20) models[0].SkinningMode = SkinningMode.Compute;
        if (frame == 30) models[0].Morphs.Enabled = false;
        if (frame == 34) models[0].Morphs.Enabled = true;
        if (frame == 36) models[0].Morphs.Enabled = false;
        if (frame == 40) models[0].Morphs.Enabled = true;
        if (frame == 43)
            foreach (var mesh in sharedModel.Meshes)
                foreach (var binding in mesh.Draw.VertexBuffers) binding.Buffer.SetSerializationData(null);
        if (frame == 44)
        {
            var replacement = new Model { Skeleton = sharedModel.Skeleton, BoundingBox = sharedModel.BoundingBox, BoundingSphere = sharedModel.BoundingSphere };
            foreach (var mesh in sharedModel.Meshes) replacement.Meshes.Add(new Mesh(mesh));
            foreach (var material in sharedModel.Materials) replacement.Materials.Add(material);
            models[0].Model = replacement;
            previous[0] = -1;
            Console.WriteLine("Testing model replacement with GPU-only source vertex buffers.");
        }
        if (frame == 46) { removedOutput = lastOutputs[0]; entities[0].Remove(models[0]); }
        if (frame == 48) entities[0].Add(models[0]);
        if (frame == 52) { removedOutput = lastOutputs[0]; models[0].Enabled = false; }
        if (frame == 56) models[0].Enabled = true;
    }

    private void VerifyModelOutputReleased()
    {
        if (removedOutput == null || !removedOutput.IsDisposed) throw new Exception("Model removal/disable did not release its output buffer.");
        Console.WriteLine($"PASS model output released at frame {frame}.");
    }
}
#endif
