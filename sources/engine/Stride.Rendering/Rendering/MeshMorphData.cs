// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Rendering;

/// <summary>Shared, import-prepared morph data. Weights belong to model instances, not this asset.</summary>
[DataContract]
public enum MeshMorphLayout
{
    SparseVertexMajor, DenseMorphMajor
}

[DataContract]
public sealed class MeshMorphData
{
    public MeshMorphLayout Layout;
    public int VertexCount;
    public string[] TargetNames;
    public bool HasNormalDeltas;
    public bool HasTangentDeltas;
    public MeshMorphEntry[] Entries;
    /// <summary>Gather: contiguous entry ranges for each vertex, with an end sentinel.</summary>
    public uint[] VertexOffsets;
    public static MeshMorphData Create(int vertexCount, string[] names, IEnumerable<MeshMorphEntry> contributions,
        bool hasNormals = false, bool hasTangents = false)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(contributions);
        if (vertexCount < 0 || vertexCount == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (names.Length == 0 || names.Length > ushort.MaxValue + 1)
            throw new ArgumentException("Morph target count must be between 1 and 65536.");
        var targetNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (string.IsNullOrWhiteSpace(name) || !targetNames.Add(name))
                throw new InvalidOperationException("Morph target names must be nonempty and unique.");
        var entries = new List<MeshMorphEntry>();
        foreach (var entry in contributions)
        {
            if (entry.VertexIndex >= vertexCount || entry.ShapeIndex >= names.Length)
                throw new ArgumentException("Morph contribution index is out of range.");
            CheckFinite(entry);
            if (!entry.IsZero)
                entries.Add(entry);
        }
        entries.Sort((a, b) => a.VertexIndex != b.VertexIndex ? a.VertexIndex.CompareTo(b.VertexIndex) : a.ShapeIndex.CompareTo(b.ShapeIndex));
        var result = new MeshMorphData
        {
            VertexCount = vertexCount,
            TargetNames = (string[])names.Clone(),
            Entries = entries.ToArray(),
            HasNormalDeltas = hasNormals,
            HasTangentDeltas = hasTangents,
            VertexOffsets = new uint[vertexCount + 1],
        };
        foreach (var entry in result.Entries)
        {
            result.VertexOffsets[entry.VertexIndex + 1]++;
        }
        Prefix(result.VertexOffsets);
        result.Validate();
        return result;
    }

    /// <summary>Reject malformed serialized data before exposing it to CPU or GPU consumers.</summary>
    public void Validate()
    {
        if (VertexCount < 0 || VertexCount == int.MaxValue || TargetNames == null || TargetNames.Length == 0 || TargetNames.Length > 65536 || Entries == null)
            throw new InvalidOperationException("Invalid morph data header.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in TargetNames)
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new InvalidOperationException("Morph target names must be nonempty and unique.");
        if (Layout == MeshMorphLayout.DenseMorphMajor)
        {
            if (Entries.Length != checked(VertexCount * TargetNames.Length) || VertexOffsets == null || VertexOffsets.Length != 0)
                throw new InvalidOperationException("Invalid dense morph layout.");
            for (int shape = 0; shape < TargetNames.Length; shape++)
                for (int vertex = 0; vertex < VertexCount; vertex++)
                {
                    var entry = Entries[shape * VertexCount + vertex];
                    if (entry.VertexIndex != vertex || entry.ShapeIndex != shape)
                        throw new InvalidOperationException("Invalid dense morph record order.");
                    CheckChannels(entry);
                    CheckFinite(entry);
                }
            return;
        }
        if (Layout != MeshMorphLayout.SparseVertexMajor)
            throw new InvalidOperationException("Unsupported morph layout.");
        CheckOffsets(VertexOffsets, VertexCount + 1, Entries.Length);
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            int previous = -1;
            for (uint i = VertexOffsets[vertex]; i < VertexOffsets[vertex + 1]; i++)
            {
                var entry = Entries[i];
                if (entry.VertexIndex != vertex || entry.ShapeIndex >= TargetNames.Length || entry.ShapeIndex <= previous || entry.IsZero)
                    throw new InvalidOperationException("Invalid or duplicate morph gather contribution.");
                if ((!HasNormalDeltas && entry.HasNormal) || (!HasTangentDeltas && entry.HasTangent))
                    throw new InvalidOperationException("Morph channel flags disagree with the records.");
                CheckFinite(entry);
                previous = entry.ShapeIndex;
            }
        }
    }

    public MeshMorphData WithLayout(MeshMorphLayout layout)
    {
        Validate();
        if (layout == Layout)
            return this;
        if (layout == MeshMorphLayout.SparseVertexMajor)
            return Create(VertexCount, TargetNames, Entries, HasNormalDeltas, HasTangentDeltas);
        if (layout != MeshMorphLayout.DenseMorphMajor)
            throw new ArgumentOutOfRangeException(nameof(layout));
        var dense = new MeshMorphEntry[checked(VertexCount * TargetNames.Length)];
        for (int shape = 0; shape < TargetNames.Length; shape++)
            for (int vertex = 0; vertex < VertexCount; vertex++)
                dense[shape * VertexCount + vertex] = MeshMorphEntry.Create((uint)vertex, (ushort)shape, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        foreach (var entry in Entries)
            dense[entry.ShapeIndex * VertexCount + entry.VertexIndex] = entry;
        return new MeshMorphData
        {
            Layout = layout,
            VertexCount = VertexCount,
            TargetNames = (string[])TargetNames.Clone(),
            HasNormalDeltas = HasNormalDeltas,
            HasTangentDeltas = HasTangentDeltas,
            Entries = dense,
            VertexOffsets = Array.Empty<uint>()
        };
    }

    private void CheckChannels(MeshMorphEntry entry)
    {
        if ((!HasNormalDeltas && entry.HasNormal) || (!HasTangentDeltas && entry.HasTangent))
            throw new InvalidOperationException("Morph channel flags disagree with the records.");
    }

    /// <summary>Preserve the selected layout after vertex splitting or reordering. Map is new vertex to old vertex.</summary>
    public MeshMorphData Remap(uint[] sourceVertices)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(sourceVertices);
        var entries = new List<MeshMorphEntry>();
        for (int vertex = 0; vertex < sourceVertices.Length; vertex++)
        {
            uint source = sourceVertices[vertex];
            if (source >= VertexCount)
                throw new ArgumentException("Morph vertex remap is out of range.");
            uint start = Layout == MeshMorphLayout.DenseMorphMajor ? 0 : VertexOffsets[source];
            uint end = Layout == MeshMorphLayout.DenseMorphMajor ? (uint)TargetNames.Length : VertexOffsets[source + 1];
            for (uint i = start; i < end; i++)
            {
                var entry = Entries[Layout == MeshMorphLayout.DenseMorphMajor ? i * VertexCount + source : i];
                entry.VertexIndex = (uint)vertex;
                entries.Add(entry);
            }
        }
        return Create(sourceVertices.Length, TargetNames, entries, HasNormalDeltas, HasTangentDeltas).WithLayout(Layout);
    }

    /// <summary>Transform before the base vertex buffer changes; directions are transformed as full attributes, then subtracted.</summary>
    public MeshMorphData Transform(Matrix transform, Vector3[] baseNormals, Vector3[] baseTangents)
    {
        Validate();
        if ((HasNormalDeltas && (baseNormals == null || baseNormals.Length != VertexCount)) ||
            (HasTangentDeltas && (baseTangents == null || baseTangents.Length != VertexCount)))
            throw new ArgumentException("Morph transforms require matching base direction attributes.");
        Matrix.Invert(ref transform, out var inverse);
        Matrix.Transpose(ref inverse, out var normalMatrix);
        var entries = new MeshMorphEntry[Entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = Entries[i];
            var position = Vector3.TransformNormal(entry.PositionDelta, transform);
            var normal = HasNormalDeltas ? TransformDirection(baseNormals[entry.VertexIndex], entry.NormalDelta, normalMatrix) : Vector3.Zero;
            var tangent = HasTangentDeltas ? TransformDirection(baseTangents[entry.VertexIndex], entry.TangentDelta, normalMatrix) : Vector3.Zero;
            entries[i] = MeshMorphEntry.Create(entry.VertexIndex, entry.ShapeIndex, position, normal, tangent);
        }
        return Create(VertexCount, TargetNames, entries, HasNormalDeltas, HasTangentDeltas).WithLayout(Layout);
    }

    private static Vector3 TransformDirection(Vector3 basis, Vector3 delta, Matrix matrix)
    {
        return Vector3.Normalize(Vector3.TransformNormal(basis + delta, matrix)) - Vector3.Normalize(Vector3.TransformNormal(basis, matrix));
    }

    private static void Prefix(uint[] offsets)
    {
        for (int i = 1; i < offsets.Length; i++)
            offsets[i] = checked(offsets[i] + offsets[i - 1]);
    }

    private static void CheckOffsets(uint[] offsets, int length, int count)
    {
        if (offsets == null || offsets.Length != length || offsets[0] != 0 || offsets[^1] != count)
            throw new InvalidOperationException("Invalid morph offset table.");
        for (int i = 1; i < offsets.Length; i++)
            if (offsets[i] < offsets[i - 1] || offsets[i] > count)
                throw new InvalidOperationException("Morph offsets are not monotonic.");
    }

    private static void CheckFinite(MeshMorphEntry entry)
    {
        if (!entry.IsFinite)
            throw new InvalidOperationException("Non-finite packed morph delta.");
    }
}
