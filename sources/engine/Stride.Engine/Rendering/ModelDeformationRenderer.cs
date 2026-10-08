// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Rendering.ComputeEffect;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Rendering;

/// <summary>
/// Thread group shape of the compute deformation dispatch: vertices × instances per group.
/// </summary>
[DataContract]
public enum DeformationThreadGroup
{
    X16Y32,
    X32Y16,
    X64Y8,
}

// Rendering owns shared immutable inputs. In compute mode, instances of the same mesh share batches of up to
// BatchSize output slots, so one dispatch deforms every instance in a batch. In vertex shader mode, each
// instance only owns its morph weights, and its render meshes apply them in TransformationMorph.
internal sealed class ModelDeformationRenderer : IDisposable
{
    private readonly Dictionary<(Mesh, MeshDraw, MeshMorphData), SharedMesh> shared = new();
    private readonly Dictionary<ModelComponent, Instance[]> instances = new();
    private readonly Dictionary<(bool Dense, DeformationThreadGroup Group), ComputeEffectShader> effects = new();
    private readonly HashSet<Batch> pending = new();
    private readonly List<Instance> pendingWeights = new();
    private readonly RenderDrawContext context;
    private int batchSize = 32;
    private DeformationThreadGroup threadGroup = DeformationThreadGroup.X32Y16;
    private MeshDeformationMode mode = MeshDeformationMode.VertexShader;

    public ModelDeformationRenderer(RenderDrawContext context)
    {
        this.context = context;
    }

    public void Configure(int maxBatchSize, DeformationThreadGroup group, MeshDeformationMode deformationMode)
    {
        maxBatchSize = Math.Clamp(maxBatchSize, 1, 1024);
        if (maxBatchSize == batchSize && group == threadGroup && deformationMode == mode)
            return;
        if (deformationMode == MeshDeformationMode.Compute && (!context.GraphicsDevice.Features.HasComputeShaders || context.GraphicsDevice.Features.RequestedProfile < GraphicsProfile.Level_11_0))
            throw new NotSupportedException("Compute deformation requires graphics profile 11.0 or higher.");
        foreach (var component in instances.Keys.ToArray())
            Remove(component);
        batchSize = maxBatchSize;
        threadGroup = group;
        mode = deformationMode;
    }

    public void Remove(ModelComponent component)
    {
        if (!instances.Remove(component, out var owned))
            return;
        foreach (var instance in owned)
            if (instance != null)
                Release(instance);
    }

    private void Release(Instance instance)
    {
        var data = instance.Shared;
        instance.Dispose();
        pendingWeights.Remove(instance);
        if (instance.Batch?.Used == 0)
        {
            data.Batches.Remove(instance.Batch);
            pending.Remove(instance.Batch);
            instance.Batch.Dispose();
        }
        if (--data.Users == 0)
        {
            shared.Remove((data.Source, data.Draw, data.Data));
            data.Dispose();
        }
    }

    public void Draw(ModelComponent model, RenderModel renderModel)
    {
        bool compute = mode == MeshDeformationMode.Compute;
        if (!model.Enabled || model.Model == null)
        {
            Remove(model);
            return;
        }
        bool morphEnabled = model.Morphs.Enabled && model.HasMorphTargets;
        if (morphEnabled)
            model.PrepareMorphWeights();
        var meshes = model.Model.Meshes;
        if (instances.TryGetValue(model, out var owned) &&
            (owned.Length != meshes.Count || owned.Where((value, index) => value != null &&
                (value.Shared.Source != meshes[index] || value.Shared.Data != meshes[index].MorphTargets || value.Shared.Draw != meshes[index].Draw)).Any()))
        {
            Remove(model);
            owned = null;
        }
        if (owned == null)
            instances[model] = owned = new Instance[meshes.Count];
        for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            var mesh = meshes[meshIndex];
            bool deformMorph = morphEnabled && mesh.MorphTargets?.VertexCount > 0;
            bool deformSkin = compute && mesh.Skinning != null;
            if (!deformMorph && !deformSkin)
            {
                if (owned[meshIndex] != null)
                {
                    Release(owned[meshIndex]);
                    owned[meshIndex] = null;
                }
                continue;
            }
            var instance = owned[meshIndex];
            if (instance == null)
            {
                var key = (mesh, mesh.Draw, mesh.MorphTargets);
                if (!shared.TryGetValue(key, out var data))
                    shared[key] = data = new SharedMesh(context, mesh, compute);
                try
                {
                    Batch batch = null;
                    if (compute && (batch = data.Batches.FirstOrDefault(x => x.Used < x.Capacity)) == null)
                        data.Batches.Add(batch = new Batch(context.GraphicsDevice, data, batchSize));
                    owned[meshIndex] = instance = new Instance(context.GraphicsDevice, data, batch);
                    data.Users++;
                }
                catch { if (data.Users == 0) { shared.Remove(key); data.Dispose(); } throw; }
            }
            var info = renderModel.Materials[meshIndex];
            if (info.MeshCount == 0)
                continue;
            Matrix world = renderModel.Meshes[info.MeshStartIndex].World;
            var bones = deformSkin ? model.MeshInfos[meshIndex].BlendMatrices : Array.Empty<Matrix>();
            instance.Prepare(deformMorph ? model.GetMorphWeights(meshIndex) : instance.ZeroWeights, bones, world);
            if (compute)
            {
                instance.Mesh.Skinning = deformSkin ? null : mesh.Skinning;
                pending.Add(instance.Batch);
            }
            else
                pendingWeights.Add(instance);
            for (int pass = 0; pass < info.MeshCount; pass++)
            {
                var rendered = renderModel.Meshes[info.MeshStartIndex + pass];
                if (compute)
                {
                    rendered.Mesh = instance.Mesh;
                    rendered.ActiveMeshDraw = instance.Mesh.Draw;
                    rendered.BlendMatrices = deformSkin ? null : model.MeshInfos[meshIndex].BlendMatrices;
                }
                else
                    rendered.MorphBuffers = instance.Buffers;
                rendered.BoundingBox = new BoundingBoxExt(!deformSkin && mesh.Skinning != null ? instance.SkinnedWorldBounds(model.MeshInfos[meshIndex].BlendMatrices) : instance.WorldBounds);
            }
        }
    }

    public void Flush()
    {
        var command = context.CommandList;
        foreach (var instance in pendingWeights)
            instance.Upload(command);
        pendingWeights.Clear();
        if (pending.Count == 0)
            return;
        var timer = context.RenderContext.Services.GetService<IGpuTimestampRecorder>();
        using (timer?.BeginRegion("ControlUpload"))
            foreach (var batch in pending)
                batch.Upload(command);
        foreach (var batch in pending)
        {
            var data = batch.Shared;
            bool dense = data.Data?.Layout == MeshMorphLayout.DenseMorphMajor;
            var (groupX, groupY) = ThreadNumbers(threadGroup);
            var effect = Effect(dense, threadGroup);
            var p = effect.Parameters;
            p.Set(Keys.Input, data.Input);
            p.Set(Keys.Entries, data.Entries);
            p.Set(Keys.Offsets, data.Offsets);
            p.Set(Keys.Weights, batch.Weights);
            p.Set(Keys.Bones, batch.Bones);
            p.Set(Keys.Flags, batch.Flags);
            p.Set(Keys.Output, batch.Output);
            p.Set(Keys.VertexCount, (uint)data.Binding.Count);
            p.Set(Keys.Stride, (uint)data.Binding.Stride);
            p.Set(Keys.Position, (uint)data.PositionOffset);
            p.Set(Keys.Normal, data.NormalOffset);
            p.Set(Keys.Tangent, data.TangentOffset);
            p.Set(Keys.Indices, data.IndicesOffset);
            p.Set(Keys.BlendWeights, data.WeightsOffset);
            p.Set(Keys.BoneCount, (uint)data.BoneCount);
            p.Set(Keys.Index16, (uint)data.Index16);
            p.Set(Keys.TargetCount, (uint)data.TargetCount);
            p.Set(Keys.ActiveCount, (uint)batch.ActiveCount);
            p.Set(Keys.InstanceCount, (uint)batch.SlotCount);
            p.Set(Keys.OutputStride, (uint)batch.SlotBytes);
            int vertexGroups = (data.Binding.Count + groupX - 1) / groupX;
            if (vertexGroups > 65535)
                throw new NotSupportedException("Compute deformation supports at most 65535 vertex thread groups per mesh.");
            effect.ThreadGroupCounts = new Int3(vertexGroups, (batch.SlotCount + groupY - 1) / groupY, 1);
            command.ResourceBarrierTransition(batch.Output, BarrierLayout.UnorderedAccess);
            using (timer?.BeginRegion(dense ? "DenseSkinning" : "SparseSkinning"))
                effect.Draw(context);
            command.ResourceBarrierTransition(batch.Output, BarrierLayout.Common);
            batch.EndFrame();
        }
        pending.Clear();
        // Unbind compute UAVs before vertex fetch. The compositor binds its states next.
        command.ClearState();
    }

    private ComputeEffectShader Effect(bool dense, DeformationThreadGroup group)
    {
        if (effects.TryGetValue((dense, group), out var effect))
            return effect;
        var (x, y) = ThreadNumbers(group);
        effect = new ComputeEffectShader(context.RenderContext) { ShaderSourceName = dense ? "MorphSkinningDense" : "MorphSkinningSparse", ThreadNumbers = new Int3(x, y, 1) };
        effects.Add((dense, group), effect);
        return effect;
    }

    private static (int X, int Y) ThreadNumbers(DeformationThreadGroup group)
    {
        return group switch
        {
            DeformationThreadGroup.X16Y32 => (16, 32),
            DeformationThreadGroup.X32Y16 => (32, 16),
            DeformationThreadGroup.X64Y8 => (64, 8),
            _ => throw new ArgumentOutOfRangeException(nameof(group)),
        };
    }

    public void Dispose()
    {
        foreach (var component in instances.Keys.ToArray())
            Remove(component);
        foreach (var effect in effects.Values)
            effect.Dispose();
        effects.Clear();
        context.Dispose();
    }

    private sealed class SharedMesh : IDisposable
    {
        public readonly Mesh Source;
        public readonly MeshDraw Draw;
        public readonly MeshMorphData Data;
        public readonly Buffer Input;
        public Buffer Entries, Offsets;
        public readonly int PositionOffset, NormalOffset, TangentOffset, IndicesOffset, WeightsOffset, Index16, MorphFlags;
        public readonly int TargetCount, BoneCount;
        public readonly byte[] VertexBytes;
        public readonly VertexBufferBinding Binding;
        public readonly Vector3[] PositionBounds;
        public readonly double[] DeltaBounds;
        public readonly List<Batch> Batches = new();
        public int Users;

        public SharedMesh(RenderDrawContext context, Mesh mesh, bool compute)
        {
            var device = context.GraphicsDevice;
            Source = mesh;
            Draw = mesh.Draw;
            Data = mesh.MorphTargets;
            Data?.Validate();
            TargetCount = Data?.TargetNames.Length ?? 0;
            BoneCount = mesh.Skinning?.Bones.Length ?? 0;
            MeasureDeltaBounds(out PositionBounds, out DeltaBounds);
            if (!compute)
            {
                // The vertex shader reads the mesh vertices; only morph data is uploaded.
                var declarations = Draw.VertexBuffers.Select(x => x.Declaration).ToArray();
                bool HasElement(string name) => declarations.Any(x => x.VertexElements.Any(e => e.SemanticName == name && e.SemanticIndex == 0));
                MorphFlags = MeshMorphBuffers.Enabled | (Data.Layout == MeshMorphLayout.DenseMorphMajor ? MeshMorphBuffers.Dense : 0);
                if (HasElement("NORMAL"))
                    MorphFlags |= MeshMorphBuffers.Normal | (HasElement("TANGENT") ? MeshMorphBuffers.Tangent : 0);
                try { CreateMorphBuffers(context.GraphicsDevice, Draw.VertexBuffers[0].Count); }
                catch { Dispose(); throw; }
                return;
            }
            if (Draw.VertexBuffers.Length != 1)
                throw new NotSupportedException("Compute deformation requires an interleaved vertex buffer.");
            Binding = Draw.VertexBuffers[0];
            if ((Data != null && Binding.Count != Data.VertexCount) || Binding.Stride % 4 != 0)
                throw new InvalidOperationException("Deformation vertex layout/count mismatch.");
            var elements = Binding.Declaration.EnumerateWithOffsets().ToArray();
            int Offset(string name, bool required, params PixelFormat[] formats)
            {
                var matches = elements.Where(x => x.VertexElement.SemanticName == name && x.VertexElement.SemanticIndex == 0).ToArray();
                if (matches.Length == 0 && !required)
                    return -1;
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
            try
            {
                Input = Buffer.New(device, VertexBytes.AsSpan(), 4, BufferFlags.ShaderResource, PixelFormat.R32_UInt);
                CreateMorphBuffers(device, Binding.Count);
            }
            catch { Dispose(); throw; }
        }

        private void CreateMorphBuffers(GraphicsDevice device, int vertexCount)
        {
            Buffer Structured(ReadOnlySpan<byte> value) => Buffer.New(device, value.Length == 0 ? new byte[4] : value, 4, BufferFlags.StructuredBuffer | BufferFlags.ShaderResource);
            Entries = Structured(MemoryMarshal.AsBytes((Data?.Entries ?? Array.Empty<MeshMorphEntry>()).AsSpan()));
            Offsets = Structured(MemoryMarshal.AsBytes((Data?.VertexOffsets ?? new uint[vertexCount + 1]).AsSpan()));
        }

        private void MeasureDeltaBounds(out Vector3[] positionBounds, out double[] deltaBounds)
        {
            // Per-target maximum magnitudes. Finite non-negative binary16 values order like their bit patterns,
            // so maxima are taken on the masked bits and converted once.
            var positionBits = new ushort[TargetCount * 3];
            var deltaBits = new ushort[TargetCount];
            foreach (var entry in Data?.Entries ?? Array.Empty<MeshMorphEntry>())
            {
                if (entry.IsZero)
                    continue;
                int shape = entry.ShapeIndex;
                uint px = entry.ShapeAndPositionX >> 16 & 0x7FFF, py = entry.PositionYZ & 0x7FFF, pz = entry.PositionYZ >> 16 & 0x7FFF;
                uint other = Math.Max(Math.Max(Math.Max(entry.NormalXY & 0x7FFF, entry.NormalXY >> 16 & 0x7FFF), Math.Max(entry.NormalZTangentX & 0x7FFF, entry.NormalZTangentX >> 16 & 0x7FFF)),
                    Math.Max(entry.TangentYZ & 0x7FFF, entry.TangentYZ >> 16 & 0x7FFF));
                positionBits[shape * 3] = (ushort)Math.Max(positionBits[shape * 3], px);
                positionBits[shape * 3 + 1] = (ushort)Math.Max(positionBits[shape * 3 + 1], py);
                positionBits[shape * 3 + 2] = (ushort)Math.Max(positionBits[shape * 3 + 2], pz);
                deltaBits[shape] = (ushort)Math.Max(deltaBits[shape], Math.Max(Math.Max(px, py), Math.Max(pz, other)));
            }
            static float Half(ushort bits) => (float)BitConverter.UInt16BitsToHalf(bits);
            positionBounds = new Vector3[TargetCount];
            deltaBounds = new double[TargetCount];
            for (int shape = 0; shape < TargetCount; shape++)
            {
                positionBounds[shape] = new Vector3(Half(positionBits[shape * 3]), Half(positionBits[shape * 3 + 1]), Half(positionBits[shape * 3 + 2]));
                deltaBounds[shape] = Half(deltaBits[shape]);
            }
        }
        public void Dispose()
        {
            Input?.Dispose();
            Entries?.Dispose();
            Offsets?.Dispose();
        }
    }

    // A fixed set of output slots for instances of one shared mesh, deformed by one dispatch.
    private sealed class Batch : IDisposable
    {
        private const uint Active = 1, Skinned = 2;
        public readonly SharedMesh Shared;
        public readonly int Capacity, SlotBytes, BoneRowsPerSlot;
        public readonly Buffer Output, Weights, Bones, Flags;
        public readonly float[] WeightData;
        public readonly Vector4[] BoneData;
        public readonly uint[] FlagData;
        private readonly bool[] occupied;
        public int Used, SlotCount, ActiveCount;

        public Batch(GraphicsDevice device, SharedMesh shared, int capacity)
        {
            Shared = shared;
            Capacity = capacity;
            SlotBytes = shared.VertexBytes.Length;
            BoneRowsPerSlot = Math.Max(shared.BoneCount, 1) * 4;
            occupied = new bool[capacity];
            WeightData = new float[Math.Max(capacity * shared.TargetCount, 1)];
            BoneData = new Vector4[capacity * BoneRowsPerSlot];
            FlagData = new uint[capacity];
            var initial = new byte[checked(capacity * SlotBytes)];
            for (int slot = 0; slot < capacity; slot++)
                shared.VertexBytes.CopyTo(initial, slot * SlotBytes);
            try
            {
                Output = Buffer.New(device, initial.AsSpan(), 4, BufferFlags.VertexBuffer | BufferFlags.ShaderResource | BufferFlags.UnorderedAccess, PixelFormat.R32_UInt, GraphicsResourceUsage.Default);
                Weights = Buffer.New(device, WeightData.Length * 4, 4, BufferFlags.StructuredBuffer | BufferFlags.ShaderResource, PixelFormat.None);
                Bones = Buffer.New(device, BoneData.Length * 16, 16, BufferFlags.StructuredBuffer | BufferFlags.ShaderResource, PixelFormat.None);
                Flags = Buffer.New(device, FlagData.Length * 4, 4, BufferFlags.StructuredBuffer | BufferFlags.ShaderResource, PixelFormat.None);
            }
            catch { Dispose(); throw; }
        }

        public int Allocate()
        {
            int slot = Array.IndexOf(occupied, false);
            occupied[slot] = true;
            Used++;
            return slot;
        }

        public void Free(int slot)
        {
            occupied[slot] = false;
            FlagData[slot] = 0;
            Used--;
        }

        public void Set(int slot, float[] values, Vector4[] rows, int boneCount, int activeCount)
        {
            values.AsSpan().CopyTo(WeightData.AsSpan(slot * Shared.TargetCount, Shared.TargetCount));
            if (boneCount != 0)
                rows.AsSpan(0, boneCount * 4).CopyTo(BoneData.AsSpan(slot * BoneRowsPerSlot));
            FlagData[slot] = Active | (boneCount != 0 ? Skinned : 0);
            SlotCount = Math.Max(SlotCount, slot + 1);
            ActiveCount = Math.Max(ActiveCount, activeCount);
        }

        public void Upload(CommandList command)
        {
            if (Shared.TargetCount != 0)
                Weights.SetData(command, WeightData.AsSpan());
            if (Shared.BoneCount != 0)
                Bones.SetData(command, BoneData.AsSpan());
            Flags.SetData(command, FlagData.AsSpan());
        }

        // Slots not prepared next frame keep their last output and are skipped by the dispatch.
        public void EndFrame()
        {
            Array.Clear(FlagData);
            SlotCount = 0;
            ActiveCount = 0;
        }

        public void Dispose()
        {
            Output?.Dispose();
            Weights?.Dispose();
            Bones?.Dispose();
            Flags?.Dispose();
        }
    }

    private sealed class Instance : IDisposable
    {
        public readonly SharedMesh Shared;
        public readonly Batch Batch;
        public readonly int Slot;
        public readonly Mesh Mesh;
        public readonly MeshMorphBuffers Buffers;
        public BoundingBox WorldBounds;
        private BoundingBox expandedBounds;
        public readonly float[] ZeroWeights;
        private readonly Matrix[] matrices;
        private readonly Vector4[] rows;
        private float[] weights;

        public Instance(GraphicsDevice device, SharedMesh shared, Batch batch)
        {
            Shared = shared;
            Batch = batch;
            matrices = new Matrix[Math.Max(shared.BoneCount, 1)];
            rows = new Vector4[matrices.Length * 4];
            ZeroWeights = new float[shared.TargetCount];
            if (batch == null)
            {
                Buffers = new MeshMorphBuffers
                {
                    Entries = shared.Entries,
                    VertexOffsets = shared.Offsets,
                    Weights = Buffer.New(device, Math.Max(shared.TargetCount, 1) * 4, 4, BufferFlags.StructuredBuffer | BufferFlags.ShaderResource, PixelFormat.None),
                    Flags = shared.MorphFlags,
                    TargetCount = shared.TargetCount,
                    VertexCount = shared.Source.Draw.VertexBuffers[0].Count,
                };
                return;
            }
            Slot = batch.Allocate();
            var source = shared.Source;
            Mesh = new Mesh(source)
            {
                Skinning = null,
                Draw = new MeshDraw
                {
                    PrimitiveType = source.Draw.PrimitiveType,
                    DrawCount = source.Draw.DrawCount,
                    StartLocation = source.Draw.StartLocation,
                    IndexBuffer = source.Draw.IndexBuffer,
                    VertexBuffers = [new VertexBufferBinding(batch.Output, shared.Binding.Declaration, shared.Binding.Count, shared.Binding.Stride, Slot * batch.SlotBytes)]
                }
            };
        }

        public void Prepare(float[] values, Matrix[] bones, Matrix world)
        {
            Vector3 expansion = Vector3.Zero;
            double bound = 0;
            int activeCount = 0;
            for (int shape = 0; shape < values.Length; shape++)
            {
                float magnitude = Math.Abs(values[shape]);
                expansion += Shared.PositionBounds[shape] * magnitude;
                bound += Shared.DeltaBounds[shape] * magnitude;
                if (values[shape] == 0)
                    continue;
                activeCount++;
            }
            if (!double.IsFinite(bound) || bound > float.MaxValue || !float.IsFinite(expansion.X + expansion.Y + expansion.Z))
                throw new InvalidOperationException("Morph accumulation exceeds finite Float32 bounds.");
            var expanded = expandedBounds = new BoundingBox(Shared.Source.BoundingBox.Minimum - expansion, Shared.Source.BoundingBox.Maximum + expansion);
            if (bones.Length > matrices.Length)
                throw new InvalidOperationException("Skinning bone layout changed.");
            Matrix.Invert(ref world, out var inverseWorld);
            if (Math.Abs(world.Determinant()) < 1e-12f)
                throw new InvalidOperationException("Morph skinning requires a nonsingular mesh transform.");
            if (bones.Length == 0)
                BoundingBox.Transform(ref expanded, ref world, out WorldBounds);
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
            if (Batch != null)
                Batch.Set(Slot, values, rows, bones.Length, activeCount);
            else
                weights = values;
        }

        public void Upload(CommandList command)
        {
            if (weights.Length != 0)
                Buffers.Weights.SetData(command, weights.AsSpan());
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

        public void Dispose()
        {
            Batch?.Free(Slot);
            Buffers?.Weights.Dispose();
        }
    }

    private static class Keys
    {
        private static ObjectParameterKey<Buffer> O(string name)
        {
            return ParameterKeys.NewObject<Buffer>(name: "MorphGpuCommon." + name);
        }

        private static ValueParameterKey<T> V<T>(string name) where T : struct
        {
            return ParameterKeys.NewValue<T>(name: "MorphGpuCommon." + name);
        }

        public static readonly ObjectParameterKey<Buffer> Input = O("InputVertices"), Entries = O("PackedEntries"), Offsets = O("VertexOffsets"), Weights = O("Weights"), Bones = O("BoneRows"), Flags = O("InstanceFlags"), Output = O("OutputVertices");
        public static readonly ValueParameterKey<uint> VertexCount = V<uint>("VertexCount"), Stride = V<uint>("VertexStride"), Position = V<uint>("PositionOffset"), BoneCount = V<uint>("BoneCount"), Index16 = V<uint>("Index16"), TargetCount = V<uint>("TargetCount"), ActiveCount = V<uint>("ActiveCount"), InstanceCount = V<uint>("InstanceCount"), OutputStride = V<uint>("OutputStride");
        public static readonly ValueParameterKey<int> Normal = V<int>("NormalOffset"), Tangent = V<int>("TangentOffset"), Indices = V<int>("BlendIndicesOffset"), BlendWeights = V<int>("BlendWeightsOffset");
    }
}
