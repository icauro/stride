// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Core;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Rendering.ComputeEffect;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Rendering;

// Rendering owns shared immutable inputs and one mutable output per model instance/mesh.
internal sealed class ModelDeformationRenderer : IDisposable
{
    private readonly Dictionary<(Mesh, MeshDraw, MeshMorphData), SharedMesh> shared = new();
    private readonly Dictionary<ModelComponent, Instance[]> instances = new();
    private readonly RenderDrawContext context;
    private readonly ComputeEffectShader denseSkinning, sparseSkinning;

    public ModelDeformationRenderer(RenderDrawContext context)
    {
        this.context = context;
        if (!context.GraphicsDevice.Features.HasComputeShaders || context.GraphicsDevice.Features.RequestedProfile < GraphicsProfile.Level_11_0)
            throw new NotSupportedException("Compute deformation requires graphics profile 11.0 or higher.");
        denseSkinning = new ComputeEffectShader(context.RenderContext) { ShaderSourceName = "MorphSkinningDense", ThreadNumbers = new Int3(64, 1, 1) };
        sparseSkinning = new ComputeEffectShader(context.RenderContext) { ShaderSourceName = "MorphSkinningSparse", ThreadNumbers = new Int3(64, 1, 1) };
    }

    public void Remove(ModelComponent component)
    {
        if (!instances.Remove(component, out var owned)) return;
        foreach (var instance in owned)
        {
            if (instance == null) continue;
            instance.Dispose();
            if (--instance.Shared.Users == 0) { shared.Remove((instance.Shared.Source, instance.Shared.Draw, instance.Shared.Data)); instance.Shared.Dispose(); }
        }
    }

    public void Draw(ModelComponent model, RenderModel renderModel, bool computeSkinning)
    {
        if (!model.Enabled || model.Model == null) { Remove(model); return; }
        bool morphEnabled = model.Morphs.Enabled && model.HasMorphTargets;
        if (morphEnabled) model.PrepareMorphWeights();
        var meshes = model.Model.Meshes;
        if (instances.TryGetValue(model, out var owned) &&
            (owned.Length != meshes.Count || owned.Where((value, index) => value != null &&
                (value.Shared.Source != meshes[index] || value.Shared.Data != meshes[index].MorphTargets || value.Shared.Draw != meshes[index].Draw)).Any()))
        { Remove(model); owned = null; }
        if (owned == null) instances[model] = owned = new Instance[meshes.Count];
        for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            var mesh = meshes[meshIndex];
            bool deformMorph = morphEnabled && mesh.MorphTargets?.VertexCount > 0;
            bool deformSkin = computeSkinning && mesh.Skinning != null;
            if (!deformMorph && !deformSkin)
            {
                if (owned[meshIndex] != null)
                {
                    var removed = owned[meshIndex]; owned[meshIndex] = null;
                    removed.Dispose();
                    if (--removed.Shared.Users == 0) { shared.Remove((removed.Shared.Source, removed.Shared.Draw, removed.Shared.Data)); removed.Shared.Dispose(); }
                }
                continue;
            }
            var instance = owned[meshIndex];
            if (instance == null)
            {
                var key = (mesh, mesh.Draw, mesh.MorphTargets);
                if (!shared.TryGetValue(key, out var data)) shared[key] = data = new SharedMesh(context, mesh);
                try { owned[meshIndex] = instance = new Instance(context.GraphicsDevice, data); data.Users++; }
                catch { if (data.Users == 0) { shared.Remove(key); data.Dispose(); } throw; }
            }
            var info = renderModel.Materials[meshIndex];
            if (info.MeshCount == 0) continue;
            Matrix world = renderModel.Meshes[info.MeshStartIndex].World;
            var bones = deformSkin ? model.MeshInfos[meshIndex].BlendMatrices : Array.Empty<Matrix>();
            instance.Mesh.Skinning = deformSkin ? null : mesh.Skinning;
            instance.Dispatch(context, mesh.MorphTargets?.Layout == MeshMorphLayout.DenseMorphMajor ? denseSkinning : sparseSkinning, deformMorph ? model.GetMorphWeights(meshIndex) : instance.ZeroWeights, bones, world);
            for (int pass = 0; pass < info.MeshCount; pass++)
            {
                var rendered = renderModel.Meshes[info.MeshStartIndex + pass];
                rendered.Mesh = instance.Mesh;
                rendered.ActiveMeshDraw = instance.Mesh.Draw;
                rendered.BlendMatrices = deformSkin ? null : model.MeshInfos[meshIndex].BlendMatrices;
                rendered.BoundingBox = new BoundingBoxExt(!deformSkin && mesh.Skinning != null ? instance.SkinnedWorldBounds(model.MeshInfos[meshIndex].BlendMatrices) : instance.WorldBounds);
            }
        }
        // Unbind compute UAVs before vertex fetch. The compositor binds its states next.
        context.CommandList.ClearState();
    }

    public void Dispose()
    {
        foreach (var component in instances.Keys.ToArray()) Remove(component);
        denseSkinning.Dispose(); sparseSkinning.Dispose(); context.Dispose();
    }

    private sealed class SharedMesh : IDisposable
    {
        public readonly Mesh Source;
        public readonly MeshDraw Draw;
        public readonly MeshMorphData Data;
        public readonly Buffer Input, Entries, Offsets;
        public readonly int PositionOffset, NormalOffset, TangentOffset, IndicesOffset, WeightsOffset, Index16;
        public readonly byte[] VertexBytes;
        public readonly VertexBufferBinding Binding;
        public readonly Vector3[] PositionBounds;
        public readonly double[] DeltaBounds;
        public int Users;

        public SharedMesh(RenderDrawContext context, Mesh mesh)
        {
            var device = context.GraphicsDevice;
            Source = mesh; Draw = mesh.Draw; Data = mesh.MorphTargets;
            Data?.Validate();
            if (Draw.VertexBuffers.Length != 1) throw new NotSupportedException("Compute deformation requires an interleaved vertex buffer.");
            Binding = Draw.VertexBuffers[0];
            if ((Data != null && Binding.Count != Data.VertexCount) || Binding.Stride % 4 != 0) throw new InvalidOperationException("Deformation vertex layout/count mismatch.");
            var elements = Binding.Declaration.EnumerateWithOffsets().ToArray();
            int Offset(string name, bool required, params PixelFormat[] formats)
            {
                var matches = elements.Where(x => x.VertexElement.SemanticName == name && x.VertexElement.SemanticIndex == 0).ToArray();
                if (matches.Length == 0 && !required) return -1;
                if (matches.Length != 1 || matches[0].Offset % 4 != 0 || !formats.Contains(matches[0].VertexElement.Format))
                    throw new NotSupportedException($"Unsupported compute deformation vertex attribute: {name}.");
                return matches[0].Offset;
            }
            PositionOffset = Offset("POSITION", true, PixelFormat.R32G32B32_Float);
            NormalOffset = Offset("NORMAL", false, PixelFormat.R32G32B32_Float);
            TangentOffset = Offset("TANGENT", false, PixelFormat.R32G32B32_Float, PixelFormat.R32G32B32A32_Float);
            IndicesOffset = Offset("BLENDINDICES", mesh.Skinning != null, PixelFormat.R8G8B8A8_UInt, PixelFormat.R16G16B16A16_UInt);
            WeightsOffset = Offset("BLENDWEIGHT", mesh.Skinning != null, PixelFormat.R32G32B32A32_Float);
            Index16 = elements.Any(x => x.VertexElement.SemanticName == "BLENDINDICES" && x.VertexElement.Format == PixelFormat.R16G16B16A16_UInt) ? 1 : 0;
            // Runtime content loading discards serialized vertex bytes. Read them
            // back once when preparing this shared mesh, never during normal updates.
            var bytes = Binding.Buffer.GetSerializationData()?.Content ?? Binding.Buffer.GetData<byte>(context.CommandList);
            VertexBytes = bytes.AsSpan(Binding.Offset, checked(Binding.Count * Binding.Stride)).ToArray();
            PositionBounds = new Vector3[Data?.TargetNames.Length ?? 0];
            DeltaBounds = new double[PositionBounds.Length];
            foreach (var entry in Data?.Entries ?? Array.Empty<MeshMorphEntry>())
            {
                Vector3 Abs(Vector3 value) => new(Math.Abs(value.X), Math.Abs(value.Y), Math.Abs(value.Z));
                var p = Abs(entry.PositionDelta);
                PositionBounds[entry.ShapeIndex] = Vector3.Max(PositionBounds[entry.ShapeIndex], p);
                var n = Abs(entry.NormalDelta); var t = Abs(entry.TangentDelta);
                DeltaBounds[entry.ShapeIndex] = Math.Max(DeltaBounds[entry.ShapeIndex], Math.Max(Math.Max(p.X, Math.Max(p.Y, p.Z)), Math.Max(Math.Max(n.X, Math.Max(n.Y, n.Z)), Math.Max(t.X, Math.Max(t.Y, t.Z)))));
            }
            try
            {
                Buffer Structured(ReadOnlySpan<byte> value) => Buffer.New(device, value.Length == 0 ? new byte[4] : value, 4, BufferFlags.StructuredBuffer | BufferFlags.ShaderResource);
                Input = Buffer.New(device, VertexBytes.AsSpan(), 4, BufferFlags.ShaderResource, PixelFormat.R32_UInt);
                Entries = Structured(MemoryMarshal.AsBytes((Data?.Entries ?? Array.Empty<MeshMorphEntry>()).AsSpan()));
                Offsets = Structured(MemoryMarshal.AsBytes((Data?.VertexOffsets ?? new uint[Binding.Count + 1]).AsSpan()));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Input?.Dispose(); Entries?.Dispose(); Offsets?.Dispose(); }
    }

    private sealed class Instance : IDisposable
    {
        public readonly SharedMesh Shared;
        public readonly Mesh Mesh;
        public BoundingBox WorldBounds;
        private BoundingBox expandedBounds;
        public readonly float[] ZeroWeights;
        private readonly Buffer weights, boneRows, output;
        private readonly Matrix[] matrices;
        private readonly Vector4[] rows;
        private readonly GraphicsDevice device;

        public Instance(GraphicsDevice device, SharedMesh shared)
        {
            this.device = device; Shared = shared;
            matrices = new Matrix[Math.Max(shared.Source.Skinning?.Bones.Length ?? 0, 1)];
            rows = new Vector4[matrices.Length * 4];
            ZeroWeights = new float[shared.Data?.TargetNames.Length ?? 0];
            try
            {
                weights = Buffer.New(device, Math.Max(ZeroWeights.Length, 1) * 4, 4, BufferFlags.StructuredBuffer | BufferFlags.ShaderResource, PixelFormat.None);
                boneRows = Buffer.New(device, matrices.Length * 64, 16, BufferFlags.StructuredBuffer | BufferFlags.ShaderResource, PixelFormat.None);
                output = Buffer.New(device, shared.VertexBytes.AsSpan(), 4, BufferFlags.VertexBuffer | BufferFlags.ShaderResource | BufferFlags.UnorderedAccess, PixelFormat.R32_UInt, GraphicsResourceUsage.Default);
                var source = shared.Source;
                Mesh = new Mesh(source) { Skinning = null, Draw = new MeshDraw {
                    PrimitiveType = source.Draw.PrimitiveType, DrawCount = source.Draw.DrawCount, StartLocation = source.Draw.StartLocation,
                    IndexBuffer = source.Draw.IndexBuffer, VertexBuffers = [new VertexBufferBinding(output, shared.Binding.Declaration, shared.Binding.Count, shared.Binding.Stride)] } };
            }
            catch { Dispose(); throw; }
        }

        public void Dispatch(RenderDrawContext context, ComputeEffectShader skinning, float[] values, Matrix[] bones, Matrix world)
        {
            var command = context.CommandList;
            var data = Shared.Data;
            Vector3 expansion = Vector3.Zero;
            double bound = 0;
            int activeCount = 0;
            for (int shape = 0; shape < values.Length; shape++)
            {
                float magnitude = Math.Abs(values[shape]);
                expansion += Shared.PositionBounds[shape] * magnitude;
                bound += Shared.DeltaBounds[shape] * magnitude;
                if (values[shape] == 0) continue;
                activeCount++;
            }
            if (!double.IsFinite(bound) || bound > float.MaxValue || !float.IsFinite(expansion.X + expansion.Y + expansion.Z)) throw new InvalidOperationException("Morph accumulation exceeds finite Float32 bounds.");
            var expanded = expandedBounds = new BoundingBox(Shared.Source.BoundingBox.Minimum - expansion, Shared.Source.BoundingBox.Maximum + expansion);
            if (bones.Length > matrices.Length) throw new InvalidOperationException("Skinning bone layout changed.");
            Matrix.Invert(ref world, out var inverseWorld);
            if (Math.Abs(world.Determinant()) < 1e-12f) throw new InvalidOperationException("Morph skinning requires a nonsingular mesh transform.");
            if (bones.Length == 0) BoundingBox.Transform(ref expanded, ref world, out WorldBounds);
            else
            {
                for (int bone = 0; bone < bones.Length; bone++)
                {
                    matrices[bone] = bones[bone] * inverseWorld;
                    // Stride matrices are stored column-major. Pack logical rows
                    // explicitly so the shader layout remains independent of that.
                    rows[bone * 4] = matrices[bone].Row1;
                    rows[bone * 4 + 1] = matrices[bone].Row2;
                    rows[bone * 4 + 2] = matrices[bone].Row3;
                    rows[bone * 4 + 3] = matrices[bone].Row4;
                    BoundingBox.Transform(ref expanded, ref bones[bone], out var transformed);
                    WorldBounds = bone == 0 ? transformed : BoundingBox.Merge(WorldBounds, transformed);
                }
            }
            var timer = context.RenderContext.Services.GetService<IGpuTimestampRecorder>();
            using (timer?.BeginRegion("ControlUpload"))
            {
                if (values.Length != 0) weights.SetData(command, values.AsSpan());
                boneRows.SetData(command, rows.AsSpan());
            }
            void Bind(ComputeEffectShader effect)
            {
                var p = effect.Parameters;
                p.Set(Keys.Input, Shared.Input); p.Set(Keys.Entries, Shared.Entries); p.Set(Keys.Offsets, Shared.Offsets);
                p.Set(Keys.Weights, weights); p.Set(Keys.Bones, boneRows);
                p.Set(Keys.Output, Mesh.Draw.VertexBuffers[0].Buffer);
                p.Set(Keys.VertexCount, (uint)Shared.Binding.Count); p.Set(Keys.Stride, (uint)Shared.Binding.Stride);
                p.Set(Keys.Position, (uint)Shared.PositionOffset); p.Set(Keys.Normal, Shared.NormalOffset); p.Set(Keys.Tangent, Shared.TangentOffset);
                p.Set(Keys.Indices, Shared.IndicesOffset); p.Set(Keys.BlendWeights, Shared.WeightsOffset);
                p.Set(Keys.BoneCount, (uint)bones.Length); p.Set(Keys.Index16, (uint)Shared.Index16); p.Set(Keys.TargetCount, (uint)values.Length);
                p.Set(Keys.ActiveCount, (uint)activeCount);
            }
            command.ResourceBarrierTransition(Mesh.Draw.VertexBuffers[0].Buffer, BarrierLayout.UnorderedAccess);
            Bind(skinning);
            uint skinGroups = ((uint)Shared.Binding.Count + 63) / 64;
            skinning.ThreadGroupCounts = new Int3((int)Math.Min(skinGroups, 65535u), (int)((skinGroups + 65534) / 65535), 1);
            using (timer?.BeginRegion(data?.Layout == MeshMorphLayout.DenseMorphMajor ? "DenseSkinning" : "SparseSkinning")) skinning.Draw(context);
            command.ResourceBarrierTransition(Mesh.Draw.VertexBuffers[0].Buffer, BarrierLayout.Common);
        }

        public BoundingBox SkinnedWorldBounds(Matrix[] bones)
        {
            var result = WorldBounds;
            for (int bone = 0; bone < bones.Length; bone++)
            {
                BoundingBox.Transform(ref expandedBounds, ref bones[bone], out var transformed);
                result = bone == 0 ? transformed : BoundingBox.Merge(result, transformed);
            }
            return result;
        }

        public void Dispose() { weights?.Dispose(); boneRows?.Dispose(); output?.Dispose(); }
    }

    private static class Keys
    {
        private static ObjectParameterKey<Buffer> O(string name) => ParameterKeys.NewObject<Buffer>(name: "MorphGpuCommon." + name);
        private static ValueParameterKey<T> V<T>(string name) where T : struct => ParameterKeys.NewValue<T>(name: "MorphGpuCommon." + name);
        public static readonly ObjectParameterKey<Buffer> Input = O("InputVertices"), Entries = O("PackedEntries"), Offsets = O("VertexOffsets"), Weights = O("Weights"), Bones = O("BoneRows"), Output = O("OutputVertices");
        public static readonly ValueParameterKey<uint> VertexCount = V<uint>("VertexCount"), Stride = V<uint>("VertexStride"), Position = V<uint>("PositionOffset"), BoneCount = V<uint>("BoneCount"), Index16 = V<uint>("Index16"), TargetCount = V<uint>("TargetCount"), ActiveCount = V<uint>("ActiveCount");
        public static readonly ValueParameterKey<int> Normal = V<int>("NormalOffset"), Tangent = V<int>("TangentOffset"), Indices = V<int>("BlendIndicesOffset"), BlendWeights = V<int>("BlendWeightsOffset");
    }
}
