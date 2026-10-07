// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Stride.Animations;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Extensions;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Importer.ThreeD;
using Stride.Rendering;
using Xunit;

namespace Stride.Assets.Tests;

public class TestMorphImport
{
    private static MeshMorphData Sample()
    {
        return MeshMorphData.Create(3, new[] { "Smile", "Blink" }, new[] {
        MeshMorphEntry.Create(2, 0, Vector3.UnitZ, Vector3.Zero, Vector3.Zero),
        MeshMorphEntry.Create(0, 1, Vector3.UnitX, Vector3.Zero, Vector3.Zero),
        MeshMorphEntry.Create(0, 0, Vector3.UnitY, Vector3.Zero, Vector3.Zero) });
    }

    [Fact]
    public void PackedBytesAndDenseSparseTraversalsAgree()
    {
        var entry = MeshMorphEntry.Create(1234, 42, new Vector3(1, 2, 3), new Vector3(4, 5, 6), new Vector3(7, 8, 9));
        Assert.Equal(24, Marshal.SizeOf<MeshMorphEntry>());
        var bytes = MemoryMarshal.AsBytes(new[] { entry }.AsSpan());
        Assert.Equal(1234u, BitConverter.ToUInt32(bytes[..4]));
        Assert.Equal((ushort)42, BitConverter.ToUInt16(bytes.Slice(4, 2)));
        for (int i = 0; i < 9; i++)
            Assert.Equal((System.Half)(i + 1), BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes.Slice(6 + i * 2, 2))));
        var data = Sample();
        Assert.Equal(new uint[] { 0, 2, 2, 3 }, data.VertexOffsets);
        var gather = new Vector3[3];
        var denseResult = new Vector3[3];
        var weights = new[] { 0.5f, 0.25f };
        for (int vertex = 0; vertex < 3; vertex++)
            for (uint i = data.VertexOffsets[vertex]; i < data.VertexOffsets[vertex + 1]; i++)
                gather[vertex] += data.Entries[i].PositionDelta * weights[data.Entries[i].ShapeIndex];
        var dense = data.WithLayout(MeshMorphLayout.DenseMorphMajor);
        dense.Validate();
        Assert.Empty(dense.VertexOffsets);
        Assert.Equal(6, dense.Entries.Length);
        for (int shape = 0; shape < 2; shape++)
            for (int vertex = 0; vertex < 3; vertex++)
                denseResult[vertex] += dense.Entries[shape * 3 + vertex].PositionDelta * weights[shape];
        Assert.Equal(data.Entries, dense.WithLayout(MeshMorphLayout.SparseVertexMajor).Entries);
        Assert.Equal(MeshMorphLayout.DenseMorphMajor, dense.Remap(new uint[] { 2, 0 }).Layout);
        Assert.Equal(gather, denseResult);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0), gather[0]);
    }

    [Theory]
    [InlineData(MeshMorphLayout.SparseVertexMajor)]
    [InlineData(MeshMorphLayout.DenseMorphMajor)]
    public void MeshSerializationPreservesPackedBytesAndIndices(MeshMorphLayout layout)
    {
        var mesh = new Mesh { Name = "Morph mesh", MorphTargets = Sample().WithLayout(layout) };
        using var stream = new MemoryStream();
        var writer = new BinarySerializationWriter(stream);
        writer.Serialize(ref mesh, ArchiveMode.Serialize);
        stream.Position = 0;
        var reader = new BinarySerializationReader(stream);
        Mesh restored = null;
        reader.Serialize(ref restored, ArchiveMode.Deserialize);
        restored.MorphTargets.Validate();
        Assert.Equal(mesh.MorphTargets.TargetNames, restored.MorphTargets.TargetNames);
        Assert.Equal(mesh.MorphTargets.Layout, restored.MorphTargets.Layout);
        Assert.Equal(MemoryMarshal.AsBytes(mesh.MorphTargets.Entries.AsSpan()).ToArray(), MemoryMarshal.AsBytes(restored.MorphTargets.Entries.AsSpan()).ToArray());
        Assert.Same(mesh.MorphTargets, new Mesh(mesh).MorphTargets);
    }

    [Fact]
    public void RejectsMalformedDataAndDropsEncodedZeros()
    {
        Assert.Throws<ArgumentException>(() => MeshMorphEntry.Create(0, 0, new Vector3(float.NaN, 0, 0), Vector3.Zero, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => MeshMorphEntry.Create(0, 0, new Vector3(65505, 0, 0), Vector3.Zero, Vector3.Zero));
        Assert.Throws<InvalidOperationException>(() => MeshMorphData.Create(3, new[] { "Same", "Same" }, Array.Empty<MeshMorphEntry>()));
        var tiny = MeshMorphEntry.Create(0, 0, new Vector3(1e-10f, 0, 0), Vector3.Zero, Vector3.Zero);
        Assert.Empty(MeshMorphData.Create(1, new[] { "Tiny" }, new[] { tiny }).Entries);
        var normalOnly = MeshMorphEntry.Create(0, 0, Vector3.Zero, Vector3.UnitY, Vector3.Zero);
        Assert.Single(MeshMorphData.Create(1, new[] { "NormalOnly" }, new[] { normalOnly }, true).Entries);
        var encodedBoundary = MeshMorphEntry.Create(0, 0, new Vector3(65504, -65504, 0), Vector3.Zero, Vector3.Zero);
        Assert.Equal(new Vector3(65504, -65504, 0), encodedBoundary.PositionDelta);
        var data = Sample();
        data = data.WithLayout(MeshMorphLayout.DenseMorphMajor);
        data.Entries[1].VertexIndex = 99;
        Assert.Throws<InvalidOperationException>(() => data.Validate());
        data = Sample();
        data.VertexOffsets[1] = 4;
        Assert.Throws<InvalidOperationException>(() => data.Validate());
        var duplicate = MeshMorphEntry.Create(0, 0, Vector3.UnitX, Vector3.Zero, Vector3.Zero);
        Assert.Throws<InvalidOperationException>(() => MeshMorphData.Create(1, new[] { "Duplicate" }, new[] { duplicate, duplicate }));
    }

    [Fact]
    public void RemappingDuplicatesContributionsAndTransformsDirectionsAsAttributes()
    {
        var remapped = Sample().Remap(new uint[] { 2, 0, 0 });
        Assert.Equal(new uint[] { 0, 1, 3, 5 }, remapped.VertexOffsets);
        Assert.Equal(remapped.Entries[1].PositionDelta, remapped.Entries[3].PositionDelta);
        var targetNormal = Vector3.Normalize(new Vector3(1, 0, 1));
        var data = MeshMorphData.Create(1, new[] { "Tilt" }, new[] {
            MeshMorphEntry.Create(0, 0, Vector3.UnitX, targetNormal - Vector3.UnitZ, Vector3.Zero) }, true);
        var transform = Matrix.Scaling(2, 3, 4) * Matrix.Translation(5, 6, 7);
        var transformed = data.Transform(transform, new[] { Vector3.UnitZ }, null);
        Assert.Equal(new Vector3(2, 0, 0), transformed.Entries[0].PositionDelta);
        // Independent analytical inverse-transpose normal result (translation must not affect a delta).
        var expected = Vector3.Normalize(new Vector3(targetNormal.X / 2, 0, targetNormal.Z / 4)) - Vector3.UnitZ;
        Assert.True((expected - transformed.Entries[0].NormalDelta).Length() < 0.002f);
    }

    [Fact]
    public void SplitMeshesRemapMorphVertexDomains()
    {
        const int count = 65538;
        var positions = new Vector3[count];
        var indices = Enumerable.Range(0, count).Select(x => (uint)x).ToArray();
        var draw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.TriangleList,
            DrawCount = count,
            VertexBuffers = new[] { new VertexBufferBinding(new BufferData(BufferFlags.VertexBuffer, MemoryMarshal.AsBytes(positions.AsSpan()).ToArray()).ToSerializableVersion(),
                new VertexDeclaration(VertexElement.Position<Vector3>()), count) },
            IndexBuffer = new IndexBufferBinding(new BufferData(BufferFlags.IndexBuffer, MemoryMarshal.AsBytes(indices.AsSpan()).ToArray()).ToSerializableVersion(), true, count)
        };
        var data = MeshMorphData.Create(count, new[] { "Move" }, new[] { MeshMorphEntry.Create(65537, 0, Vector3.UnitX, Vector3.Zero, Vector3.Zero) });
        var split = SplitExtensions.SplitMeshes(new() { new Mesh { Draw = draw, MorphTargets = data } }, false);
        Assert.Equal(2, split.Count);
        Assert.Empty(split[0].MorphTargets.Entries);
        Assert.Equal(2u, Assert.Single(split[1].MorphTargets.Entries).VertexIndex);
        foreach (var mesh in split)
            Assert.Equal(mesh.Draw.VertexBuffers[0].Count, mesh.MorphTargets.VertexCount);
    }

    [Fact]
    public void ImportsSparsePositionNormalAndTangentData()
    {
        // glTF deltas become Assimp absolute target attributes before our conversion.
        var arrays = new[] {
            new float[] { 0,0,0, 1,0,0, 0,1,0 },
            new float[] { 0,0,1, 0,0,1, 0,0,1 },
            new float[] { 1,0,0,1, 1,0,0,1, 1,0,0,1 },
            new float[] { 0,0,1, 0,0,0, 0,0,0 },
            new float[] { 0,0.25f,0, 0,0,0, 0,0,0 },
            new float[] { 0,0.25f,0, 0,0,0, 0,0,0 } };
        var bytes = MemoryMarshal.AsBytes(arrays.SelectMany(x => x).ToArray().AsSpan()).ToArray();
        var views = arrays.Select((a, i) => new { buffer = 0, byteOffset = arrays.Take(i).Sum(x => x.Length) * 4, byteLength = a.Length * 4 }).ToArray();
        var accessors = arrays.Select((a, i) => new { bufferView = i, componentType = 5126, count = 3, type = i == 2 ? "VEC4" : "VEC3" }).ToArray();
        var gltf = new
        {
            asset = new
            {
                version = "2.0"
            },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0 } },
            meshes = new[] { new { name = "Triangle", extras = new { targetNames = new[] { "Raise" } }, primitives = new[] { new {
                attributes = new { POSITION = 0, NORMAL = 1, TANGENT = 2 }, targets = new[] { new { POSITION = 3, NORMAL = 4, TANGENT = 5 } }, mode = 4 } } } },
            buffers = new[] { new { byteLength = bytes.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes) } },
            bufferViews = views,
            accessors
        };
        var path = Path.Combine(Path.GetTempPath(), $"stride-packed-morph-{Guid.NewGuid():N}.gltf");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(gltf));
            var mesh = Assert.Single(new MeshConverter(null).Convert(path, path, false).Meshes);
            var data = mesh.MorphTargets;
            data.Validate();
            Assert.Equal(3, data.VertexCount);
            Assert.True(data.HasNormalDeltas);
            Assert.True(data.HasTangentDeltas);
            var entry = Assert.Single(data.Entries);
            Assert.True(Math.Abs(entry.PositionDelta.Z) > 0.99f);
            Assert.True(entry.NormalDelta.Length() > 0.2f);
            Assert.True(entry.TangentDelta.Length() > 0.2f);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ImportsAnimatedWeightsAsCurvesByTargetName()
    {
        var arrays = new[] {
            new float[] { 0,0,0, 1,0,0, 0,1,0 },
            new float[] { 0,0,1, 0,0,0, 0,0,0 },
            new float[] { 0,1,0, 0,0,0, 0,0,0 },
            new float[] { 0, 1 },
            new float[] { 0.25f, 1, 0.75f, 0 } };
        var bytes = MemoryMarshal.AsBytes(arrays.SelectMany(x => x).ToArray().AsSpan()).ToArray();
        var views = arrays.Select((a, i) => new { buffer = 0, byteOffset = arrays.Take(i).Sum(x => x.Length) * 4, byteLength = a.Length * 4 }).ToArray();
        var accessors = arrays.Select((a, i) => i == 3
            ? (object)new
            {
                bufferView = i,
                componentType = 5126,
                count = a.Length,
                type = "SCALAR",
                min = new[] { 0f },
                max = new[] { 1f }
            }
            : new
            {
                bufferView = i,
                componentType = 5126,
                count = i < 3 ? 3 : a.Length,
                type = i < 3 ? "VEC3" : "SCALAR"
            }).ToArray();
        var gltf = new
        {
            asset = new
            {
                version = "2.0"
            },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { name = "Face", mesh = 0 } },
            meshes = new[] { new { name = "Triangle", extras = new { targetNames = new[] { "Raise", "Lift" } }, primitives = new[] { new {
                attributes = new { POSITION = 0 }, targets = new[] { new { POSITION = 1 }, new { POSITION = 2 } }, mode = 4 } } } },
            animations = new[] { new { name = "Talk", samplers = new[] { new { input = 3, output = 4, interpolation = "LINEAR" } },
                channels = new[] { new { sampler = 0, target = new { node = 0, path = "weights" } } } } },
            buffers = new[] { new { byteLength = bytes.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes) } },
            bufferViews = views,
            accessors
        };
        var path = Path.Combine(Path.GetTempPath(), $"stride-morph-animation-{Guid.NewGuid():N}.gltf");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(gltf));
            // Like bone animation, the curves land in the node's clip, one channel per target.
            var clip = new MeshConverter(null).ConvertAnimation(path, path, 0).AnimationClips["Face"];
            AnimationCurve<float> Curve(string name) => (AnimationCurve<float>)clip.Curves[clip.Channels[$"MorphWeights[{name}]"].CurveIndex];
            Assert.Equal(new[] { "MorphWeights[Lift]", "MorphWeights[Raise]" }, clip.Channels.Keys.Order());
            Assert.Equal(new[] { 0.25f, 0.75f }, Curve("Raise").KeyFrames.Select(key => key.Value));
            Assert.Equal(new[] { 1f, 0f }, Curve("Lift").KeyFrames.Select(key => key.Value));
            Assert.Equal(CompressedTimeSpan.FromSeconds(1), clip.Duration);
        }
        finally { File.Delete(path); }
    }
}
