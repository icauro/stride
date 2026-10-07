// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Rendering;
using Xunit;

namespace Stride.Assets.Tests;

public class TestModelComponentMorphs
{
    private static Model ModelWithTargets(int meshes = 1, string target = "Smile")
    {
        var model = new Model();
        for (int i = 0; i < meshes; i++)
            model.Meshes.Add(new Mesh { MorphTargets = MeshMorphData.Create(0, new[] { target }, Array.Empty<MeshMorphEntry>()) });
        return model;
    }

    [Fact]
    public void SharedModelHasIndependentNamedWeightsAndZeroDefaults()
    {
        var shared = ModelWithTargets(2);
        var first = new ModelComponent(shared);
        var second = new ModelComponent(shared);
        Assert.True(first.HasMorphTargets);
        Assert.Equal(0, first.GetMorphWeight(0, "Smile"));
        first.SetMorphWeight("Smile", -0.75f);
        Assert.Equal(-0.75f, first.GetMorphWeight(0, 0));
        Assert.Equal(-0.75f, first.GetMorphWeight(1, 0));
        Assert.Equal(0, second.GetMorphWeight(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.SetMorphWeight("Smile", float.NaN));
        Assert.Throws<ArgumentException>(() => first.SetMorphWeight("Missing", 1));
        Assert.False(new ModelComponent(new Model()).HasMorphTargets);
    }

    [Fact]
    public void SerializedControlsSurviveDelayedLoadingAndResetOnReplacement()
    {
        var component = new ModelComponent();
        component.Morphs.SetWeight("Smile", 0.125f);
        using var stream = new MemoryStream();
        var writer = new BinarySerializationWriter(stream);
        writer.Serialize(ref component, ArchiveMode.Serialize);
        stream.Position = 0;
        ModelComponent restored = null;
        var reader = new BinarySerializationReader(stream);
        reader.Serialize(ref restored, ArchiveMode.Deserialize);
        restored.Model = ModelWithTargets();
        Assert.Equal(0.125f, restored.GetMorphWeight(0, "Smile"));
        restored.Model = ModelWithTargets(target: "Blink");
        Assert.Equal(0, restored.GetMorphWeight(0, "Blink"));
        Assert.Empty(restored.Morphs.Weights);
    }

    [Fact]
    public void SetAllMorphWeightsUpdatesAllMeshesAndOnlyThisInstance()
    {
        var shared = ModelWithTargets(2);
        shared.Meshes.Add(new Mesh { MorphTargets = MeshMorphData.Create(0, new[] { "Blink" }, Array.Empty<MeshMorphEntry>()) });
        var first = new ModelComponent(shared);
        var second = new ModelComponent(shared);
        first.SetAllMorphWeights(-1f);
        Assert.Equal(-1f, first.GetMorphWeight(0, 0));
        Assert.Equal(-1f, first.GetMorphWeight(1, 0));
        Assert.Equal(-1f, first.GetMorphWeight(2, 0));
        Assert.Empty(first.Morphs.Weights); // runtime writes do not touch authored weights
        Assert.Equal(0f, second.GetMorphWeight(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.SetAllMorphWeights(float.NaN));
        Assert.Equal(-1f, first.GetMorphWeight(0, 0));
        first.SetAllMorphWeights(0f);
        Assert.Equal(0f, first.GetMorphWeight(2, 0));
        first.Model = ModelWithTargets(1, "NewTarget");
        first.SetAllMorphWeights(1f);
        Assert.Equal(1f, first.GetMorphWeight(0, 0));
        new ModelComponent().SetAllMorphWeights(1f);
    }

    [Fact]
    public void SlotsAreSharedByNameAndAuthoredWeightsSeedThem()
    {
        var shared = ModelWithTargets(2);
        shared.Meshes.Add(new Mesh { MorphTargets = MeshMorphData.Create(0, new[] { "Blink", "Smile" }, Array.Empty<MeshMorphEntry>()) });
        var component = new ModelComponent(shared);
        Assert.Equal(new[] { "Smile", "Blink" }, component.MorphTargetNames);
        int smile = component.FindMorphTarget("Smile"), blink = component.FindMorphTarget("Blink");
        Assert.Equal(-1, component.FindMorphTarget("Missing"));
        component.SetMorphWeight(smile, 0.5f);
        Assert.Equal(0.5f, component.GetMorphWeight(0, 0));
        Assert.Equal(0.5f, component.GetMorphWeight(2, 1));
        Assert.Equal(0f, component.GetMorphWeight(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => component.SetMorphWeight(5, 1));
        // An authored change re-seeds runtime weights from the authored values.
        component.Morphs.SetWeight("Blink", 0.25f);
        Assert.Equal(0.25f, component.GetMorphWeight(blink));
        Assert.Equal(0f, component.GetMorphWeight(smile));
        Assert.Same(component.MorphTargetNames, new ModelComponent(shared).MorphTargetNames);
        var weights = component.WriteMorphWeights();
        weights[smile] = 0.75f;
        Assert.Equal(0.75f, component.GetMorphWeight(2, 1));
        Assert.Equal(new[] { 0.75f, 0.25f }, component.MorphWeights.ToArray());
    }

    [Fact]
    public void NamedWeightsRoundTripThroughAssetYaml()
    {
        var scene = new Stride.Assets.Entities.SceneAsset();
        var model = new ModelComponent();
        model.Morphs.SetWeight("Smile", -0.25f);
        var entity = new Entity { model };
        scene.Hierarchy.Parts.Add(new Stride.Assets.Entities.EntityDesign(entity));
        using var stream = new MemoryStream();
        AssetFileSerializer.Save(stream, scene, null);
        stream.Position = 0;
        var restored = AssetFileSerializer.Load<Stride.Assets.Entities.SceneAsset>(stream, "scene.sdscene").Asset;
        var restoredModel = System.Linq.Enumerable.First(restored.Hierarchy.Parts).Value.Entity.Get<ModelComponent>();
        Assert.Equal(-0.25f, restoredModel.Morphs.GetWeight("Smile"));
    }
}
