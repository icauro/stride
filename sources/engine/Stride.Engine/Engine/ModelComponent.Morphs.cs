// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stride.Core;
using Stride.Rendering;

namespace Stride.Engine;

/// <summary>Authored morph settings. Runtime weights live in a slot-indexed array on <see cref="ModelComponent"/>.</summary>
[DataContract]
public sealed class ModelMorphSettings
{
    private Dictionary<string, float> weights = new(StringComparer.Ordinal);

    /// <summary>Set by embedded editor games so live edits of <see cref="Weights"/> items reach the runtime weights every frame.</summary>
    public static bool ApplyAuthoredWeightsEveryFrame
    {
        get; set;
    }

    [DataMember(10), DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Authored weights by target name: applied when the model binds and when this dictionary is replaced or changed
    /// through <see cref="SetWeight"/>. Use <see cref="ModelComponent.SetMorphWeight(int, float)"/> to drive weights at runtime.
    /// </summary>
    [DataMember(30), Display("Weights", Expand = ExpandRule.Once)]
    public Dictionary<string, float> Weights
    {
        get => weights;
        set
        {
            weights = value ?? throw new ArgumentNullException(nameof(value));
            Version++;
        }
    }

    [DataMemberIgnore]
    internal int Version
    {
        get; private set;
    }

    public void SetAllWeights(IEnumerable<string> names, float value)
    {
        ArgumentNullException.ThrowIfNull(names);
        CheckWeight(value);
        foreach (var name in names)
            SetWeight(name, value);
    }

    public float GetWeight(string name)
    {
        return Weights.TryGetValue(name, out var value) ? value : 0;
    }

    public void SetWeight(string name, float value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        CheckWeight(value);
        Weights[name] = value;
        Version++;
    }

    internal static void CheckWeight(float value)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Morph weights must be finite.");
    }

    internal void Validate()
    {
        foreach (var pair in Weights)
            if (string.IsNullOrWhiteSpace(pair.Key) || !float.IsFinite(pair.Value))
                throw new InvalidOperationException("Morph weights must have valid names and finite values.");
    }
}

/// <summary>Model-wide morph slot table: one slot per distinct target name, shared by every instance of the model.</summary>
internal sealed class ModelMorphLayout
{
    private static readonly ConditionalWeakTable<Model, ModelMorphLayout> Cache = new();

    public readonly MeshMorphData[] Sources;
    public readonly string[] Names;
    public readonly Dictionary<string, int> Slots;
    /// <summary>Per mesh: mesh-local target index to model slot.</summary>
    public readonly int[][] MeshSlots;

    private ModelMorphLayout(Model model)
    {
        Sources = new MeshMorphData[model.Meshes.Count];
        MeshSlots = new int[model.Meshes.Count][];
        Slots = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        for (int mesh = 0; mesh < Sources.Length; mesh++)
        {
            var data = Sources[mesh] = model.Meshes[mesh].MorphTargets;
            data?.Validate();
            var targetNames = data?.TargetNames ?? Array.Empty<string>();
            MeshSlots[mesh] = new int[targetNames.Length];
            for (int target = 0; target < targetNames.Length; target++)
            {
                if (!Slots.TryGetValue(targetNames[target], out int slot))
                {
                    Slots.Add(targetNames[target], slot = names.Count);
                    names.Add(targetNames[target]);
                }
                MeshSlots[mesh][target] = slot;
            }
        }
        Names = names.ToArray();
    }

    public bool Matches(Model model)
    {
        if (model.Meshes.Count != Sources.Length)
            return false;
        for (int mesh = 0; mesh < Sources.Length; mesh++)
            if (!ReferenceEquals(Sources[mesh], model.Meshes[mesh].MorphTargets))
                return false;
        return true;
    }

    public static ModelMorphLayout Get(Model model)
    {
        if (Cache.TryGetValue(model, out var layout) && layout.Matches(model))
            return layout;
        layout = new ModelMorphLayout(model);
        Cache.AddOrUpdate(model, layout);
        return layout;
    }
}

public sealed partial class ModelComponent
{
    private ModelMorphLayout morphLayout;
    private Model morphModel;
    private float[] slotWeights = Array.Empty<float>();
    private float[][] meshWeights = Array.Empty<float[]>();
    private int appliedAuthoringVersion = -1;
    private bool meshWeightsDirty = true;
    private bool morphInitialized;

    [DataMember(60), Display("Morphs", Expand = ExpandRule.Once)]
    public ModelMorphSettings Morphs { get; } = new();

    [DataMemberIgnore]
    public bool HasMorphTargets => Model?.Meshes.Exists(mesh => mesh.MorphTargets?.TargetNames.Length > 0) == true;

    /// <summary>Distinct morph target names of the model; a name's index is its slot for <see cref="SetMorphWeight(int, float)"/>.</summary>
    [DataMemberIgnore]
    public IReadOnlyList<string> MorphTargetNames
    {
        get
        {
            SynchronizeMorphTargets();
            return morphLayout?.Names ?? Array.Empty<string>();
        }
    }

    private void SynchronizeMorphTargets()
    {
        if (Model == null)
        {
            if (morphModel != null && morphInitialized)
                Morphs.Weights.Clear();
            morphModel = null;
            morphLayout = null;
            slotWeights = Array.Empty<float>();
            meshWeights = Array.Empty<float[]>();
            return;
        }
        if (ReferenceEquals(Model, morphModel) && morphLayout != null && morphLayout.Matches(Model))
            return;
        if (morphModel != null && morphInitialized && !ReferenceEquals(Model, morphModel))
            Morphs.Weights.Clear();
        morphLayout = ModelMorphLayout.Get(Model);
        morphModel = Model;
        slotWeights = new float[morphLayout.Names.Length];
        meshWeights = new float[morphLayout.MeshSlots.Length][];
        for (int mesh = 0; mesh < meshWeights.Length; mesh++)
            meshWeights[mesh] = new float[morphLayout.MeshSlots[mesh].Length];
        appliedAuthoringVersion = -1;
        meshWeightsDirty = true;
        // An editor asset proxy has no meshes; binding it must not erase serialized settings.
        morphInitialized |= Model.Meshes.Count != 0;
    }

    private void ApplyAuthoredWeights(bool force = false)
    {
        if (appliedAuthoringVersion == Morphs.Version && !force)
            return;
        Morphs.Validate();
        Array.Clear(slotWeights);
        foreach (var pair in Morphs.Weights)
            if (morphLayout.Slots.TryGetValue(pair.Key, out int slot))
                slotWeights[slot] = pair.Value;
        appliedAuthoringVersion = Morphs.Version;
        meshWeightsDirty = true;
    }

    internal void PrepareMorphWeights()
    {
        SynchronizeMorphTargets();
        if (morphLayout == null)
            return;
        ApplyAuthoredWeights(ModelMorphSettings.ApplyAuthoredWeightsEveryFrame);
        if (!meshWeightsDirty)
            return;
        for (int mesh = 0; mesh < meshWeights.Length; mesh++)
        {
            var slots = morphLayout.MeshSlots[mesh];
            var weights = meshWeights[mesh];
            for (int target = 0; target < weights.Length; target++)
                weights[target] = slotWeights[slots[target]];
        }
        meshWeightsDirty = false;
    }

    internal float[] GetMorphWeights(int mesh)
    {
        return meshWeights[mesh];
    }

    private void Bind()
    {
        SynchronizeMorphTargets();
        if (morphLayout != null)
            ApplyAuthoredWeights();
    }

    /// <summary>Returns the slot of a morph target name, or -1 when the model has no such target.</summary>
    public int FindMorphTarget(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SynchronizeMorphTargets();
        return morphLayout != null && morphLayout.Slots.TryGetValue(name, out int slot) ? slot : -1;
    }

    /// <summary>All weights, indexed by slot (see <see cref="MorphTargetNames"/>). Do not keep across a model change.</summary>
    [DataMemberIgnore]
    public ReadOnlySpan<float> MorphWeights
    {
        get
        {
            Bind();
            return slotWeights;
        }
    }

    /// <summary>Marks the weights changed and returns them for bulk writes, indexed by slot. Do not keep across frames or a model change.</summary>
    public Span<float> WriteMorphWeights()
    {
        Bind();
        meshWeightsDirty = true;
        return slotWeights;
    }

    public float GetMorphWeight(int slot)
    {
        Bind();
        if ((uint)slot >= (uint)slotWeights.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return slotWeights[slot];
    }

    public void SetMorphWeight(int slot, float value)
    {
        ModelMorphSettings.CheckWeight(value);
        Bind();
        if ((uint)slot >= (uint)slotWeights.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (slotWeights[slot] == value)
            return;
        slotWeights[slot] = value;
        meshWeightsDirty = true;
    }

    public void SetMorphWeight(string name, float value)
    {
        int slot = FindMorphTarget(name);
        if (slot < 0)
            throw new ArgumentException($"Morph target '{name}' is not in the model.", nameof(name));
        SetMorphWeight(slot, value);
    }

    public float GetMorphWeight(int mesh, int target)
    {
        return GetMorphWeight(MeshSlot(mesh, target));
    }

    public float GetMorphWeight(int mesh, string name)
    {
        return GetMorphWeight(MeshSlot(mesh, name));
    }

    public void SetMorphWeight(int mesh, int target, float value)
    {
        SetMorphWeight(MeshSlot(mesh, target), value);
    }

    public void SetMorphWeight(int mesh, string name, float value)
    {
        SetMorphWeight(MeshSlot(mesh, name), value);
    }

    public void SetAllMorphWeights(float value)
    {
        ModelMorphSettings.CheckWeight(value);
        Bind();
        Array.Fill(slotWeights, value);
        meshWeightsDirty = true;
    }

    private int MeshSlot(int mesh, int target)
    {
        SynchronizeMorphTargets();
        if (morphLayout == null || (uint)mesh >= (uint)morphLayout.MeshSlots.Length || (uint)target >= (uint)morphLayout.MeshSlots[mesh].Length)
            throw new ArgumentOutOfRangeException(nameof(target));
        return morphLayout.MeshSlots[mesh][target];
    }

    private int MeshSlot(int mesh, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SynchronizeMorphTargets();
        if (morphLayout == null || (uint)mesh >= (uint)morphLayout.MeshSlots.Length)
            throw new ArgumentOutOfRangeException(nameof(mesh));
        int index = morphLayout.Sources[mesh] == null ? -1 : Array.IndexOf(morphLayout.Sources[mesh].TargetNames, name);
        return index >= 0 ? morphLayout.MeshSlots[mesh][index] : throw new ArgumentException($"Morph target '{name}' is not in mesh {mesh}.", nameof(name));
    }
}
