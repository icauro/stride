// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Processors;
using Stride.Importer.ThreeD;
using Stride.Rendering;
using Xunit;

namespace Stride.Assets.Tests;

public class TestBlendShapes
{
    [Fact]
    public void ImportedTargetsCanBeCookedAndDeformed()
    {
        // A self-contained glTF triangle with one position target. No external model or GPU required.
        var positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
        var deltas = new float[] { 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        var bytes = MemoryMarshal.AsBytes(positions.Concat(normals).Concat(deltas).ToArray().AsSpan()).ToArray();
        var gltf = new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0 } },
            meshes = new[] { new { name = "Triangle", extras = new { targetNames = new[] { "Raise" } },
                primitives = new[] { new { attributes = new { POSITION = 0, NORMAL = 1 },
                    targets = new[] { new { POSITION = 2 } }, mode = 4 } } } },
            buffers = new[] { new { byteLength = bytes.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes) } },
            bufferViews = new[] {
                new { buffer = 0, byteOffset = 0, byteLength = 36 },
                new { buffer = 0, byteOffset = 36, byteLength = 36 },
                new { buffer = 0, byteOffset = 72, byteLength = 36 } },
            accessors = new[] {
                new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3", min = new[] { 0f, 0f, 0f }, max = new[] { 1f, 1f, 0f } },
                new { bufferView = 1, componentType = 5126, count = 3, type = "VEC3", min = new[] { 0f, 0f, 1f }, max = new[] { 0f, 0f, 1f } },
                new { bufferView = 2, componentType = 5126, count = 3, type = "VEC3", min = new[] { 0f, 0f, 0f }, max = new[] { 0f, 0f, 1f } } }
        };
        var path = Path.Combine(Path.GetTempPath(), $"stride-morph-{Guid.NewGuid():N}.gltf");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(gltf));
            var model = new MeshConverter(null).Convert(path, path, false);
            var definition = Assert.Single(model.Meshes).BlendShapes;
            Assert.NotNull(definition);
            Assert.Equal(3, definition.VertexCount);
            Assert.Equal(3, definition.BasePositions.Length);
            Assert.Equal(3, Assert.Single(definition.Targets).DeltaPositions.Length);
            definition.Cook();
            Assert.NotNull(definition.CookedData);
            Assert.Equal(1, definition.CookedData.TotalContribCount);
            var output = new byte[definition.VertexStride * definition.VertexCount];
            BlendShapeDeformer.Deform(definition, new[] { 0.5f }, output);
            var position = MemoryMarshal.Read<Vector3>(output.AsSpan(definition.PositionOffset));
            Assert.True(Math.Abs(position.Z - definition.BasePositions[0].Z) > 0.49f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(300)]
    public void CpuBlendPreservesOtherAttributesAndRestoresBase(int count)
    {
        var definition = new MeshBlendShapeDefinition
        {
            VertexCount = count, VertexStride = 28, PositionOffset = 0, NormalOffset = 12,
            BasePositions = Enumerable.Repeat(Vector3.UnitX, count).ToArray(),
            BaseNormals = Enumerable.Repeat(Vector3.UnitZ, count).ToArray(),
            Targets = new[] {
                new BlendShapeTarget { Name = "Raise", HasDeltaPositions = true, DeltaPositions = Enumerable.Repeat(Vector3.UnitY, count).ToArray() },
                new BlendShapeTarget { Name = "Move", HasDeltaPositions = true, DeltaPositions = Enumerable.Repeat(Vector3.UnitX, count).ToArray() } }
        };
        var output = Enumerable.Repeat((byte)0x5A, count * definition.VertexStride).ToArray();
        BlendShapeDeformer.Deform(definition, new[] { 0.5f, 0.25f }, output);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(new Vector3(1.25f, 0.5f, 0), MemoryMarshal.Read<Vector3>(output.AsSpan(i * 28)));
            Assert.Equal(new byte[] { 0x5A, 0x5A, 0x5A, 0x5A }, output.AsSpan(i * 28 + 24, 4).ToArray());
        }
        BlendShapeDeformer.Deform(definition, new float[2], output);
        Assert.Equal(Vector3.UnitX, MemoryMarshal.Read<Vector3>(output));
    }

    [Fact]
    public void SharedModelsHaveIndependentWeights()
    {
        var model = new Model();
        model.Meshes.Add(new Mesh { BlendShapes = new MeshBlendShapeDefinition {
            Targets = new[] { new BlendShapeTarget { Name = "Smile" } } } });
        var first = new BlendShapeComponent();
        var second = new BlendShapeComponent();
        first.InitializeFromModel(new ModelComponent(model));
        second.InitializeFromModel(new ModelComponent(model));
        first.SetWeight("Smile", 0.75f);
        Assert.Equal(0.75f, first.GetWeight("Smile"));
        Assert.Equal(0f, second.GetWeight("Smile"));
        Assert.NotSame(first.WeightValues, second.WeightValues);
    }
}
