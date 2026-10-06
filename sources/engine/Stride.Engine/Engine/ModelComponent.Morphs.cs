// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core;
using Stride.Rendering;

namespace Stride.Engine;

[DataContract]
public sealed class ModelMorphSettings
{
    private Dictionary<string, float> weights = new(StringComparer.Ordinal);

    [DataMember(10), DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    // One named weight drives matching targets across the model's meshes. Missing names are zero.
    [DataMember(30), Display("Weights", Expand = ExpandRule.Once)]
    public Dictionary<string, float> Weights
    {
        get => weights;
        set => weights = value ?? throw new ArgumentNullException(nameof(value));
    }

    public void SetAllWeights(IEnumerable<string> names, float value)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Morph weights must be finite.");
        foreach (var name in names) SetWeight(name, value);
    }

    public float GetWeight(string name) => Weights.TryGetValue(name, out var value) ? value : 0;
    public void SetWeight(string name, float value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Morph weights must be finite.");
        Weights[name] = value;
    }

    internal void Validate()
    {
        foreach (var pair in Weights)
            if (string.IsNullOrWhiteSpace(pair.Key) || !float.IsFinite(pair.Value)) throw new InvalidOperationException("Morph weights must have valid names and finite values.");
    }
}

public sealed partial class ModelComponent
{
    private Model morphModel;
    private MeshMorphData[] morphTargets = Array.Empty<MeshMorphData>();
    private float[][] morphWeights = Array.Empty<float[]>();
    private bool morphInitialized;

    [DataMember(60), Display("Morphs", Expand = ExpandRule.Once)]
    public ModelMorphSettings Morphs { get; } = new();

    [DataMemberIgnore]
    public bool HasMorphTargets => Model?.Meshes.Exists(mesh => mesh.MorphTargets?.TargetNames.Length > 0) == true;

    private void SynchronizeMorphTargets()
    {
        if (Model == null)
        {
            morphModel = null; morphTargets = Array.Empty<MeshMorphData>(); morphWeights = Array.Empty<float[]>();
            if (morphInitialized) Morphs.Weights.Clear();
            return;
        }
        bool same = ReferenceEquals(Model, morphModel) && morphTargets.Length == Model.Meshes.Count;
        for (int mesh = 0; same && mesh < morphTargets.Length; mesh++) same &= ReferenceEquals(morphTargets[mesh], Model.Meshes[mesh].MorphTargets);
        if (same) return;
        if (morphInitialized) Morphs.Weights.Clear();
        morphTargets = new MeshMorphData[Model.Meshes.Count];
        morphWeights = new float[Model.Meshes.Count][];
        for (int mesh = 0; mesh < morphTargets.Length; mesh++)
        {
            var data = Model.Meshes[mesh].MorphTargets;
            data?.Validate();
            morphTargets[mesh] = data;
            morphWeights[mesh] = new float[data?.TargetNames.Length ?? 0];
        }
        morphModel = Model;
        // An editor asset proxy has no meshes; binding it must not erase serialized settings.
        morphInitialized |= Model.Meshes.Count != 0;
    }

    internal void PrepareMorphWeights()
    {
        SynchronizeMorphTargets();
        Morphs.Validate();
        for (int mesh = 0; mesh < morphTargets.Length; mesh++)
            for (int target = 0; target < morphWeights[mesh].Length; target++)
                morphWeights[mesh][target] = Morphs.GetWeight(morphTargets[mesh].TargetNames[target]);
    }

    internal float[] GetMorphWeights(int mesh) => morphWeights[mesh];

    private string MorphTargetName(int mesh, int target)
    {
        SynchronizeMorphTargets();
        if ((uint)mesh >= morphTargets.Length || morphTargets[mesh] == null || (uint)target >= morphTargets[mesh].TargetNames.Length)
            throw new ArgumentOutOfRangeException(nameof(target));
        return morphTargets[mesh].TargetNames[target];
    }

    public float GetMorphWeight(int mesh, int target) => Morphs.GetWeight(MorphTargetName(mesh, target));
    public float GetMorphWeight(int mesh, string name) => GetMorphWeight(mesh, FindMorphTarget(mesh, name));
    public void SetMorphWeight(int mesh, int target, float value) => Morphs.SetWeight(MorphTargetName(mesh, target), value);
    public void SetMorphWeight(int mesh, string name, float value) => SetMorphWeight(mesh, FindMorphTarget(mesh, name), value);
    public void SetAllMorphWeights(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Morph weights must be finite.");
        SynchronizeMorphTargets();
        foreach (var data in morphTargets)
            if (data != null) Morphs.SetAllWeights(data.TargetNames, value);
    }
    public void SetMorphWeight(string name, float value)
    {
        SynchronizeMorphTargets();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (var data in morphTargets)
            if (data != null && Array.IndexOf(data.TargetNames, name) >= 0) { Morphs.SetWeight(name, value); return; }
        throw new ArgumentException($"Morph target '{name}' is not in the model.", nameof(name));
    }

    private int FindMorphTarget(int mesh, string name)
    {
        SynchronizeMorphTargets();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if ((uint)mesh >= morphTargets.Length) throw new ArgumentOutOfRangeException(nameof(mesh));
        int index = morphTargets[mesh] == null ? -1 : Array.IndexOf(morphTargets[mesh].TargetNames, name);
        return index >= 0 ? index : throw new ArgumentException($"Morph target '{name}' is not in mesh {mesh}.", nameof(name));
    }
}
