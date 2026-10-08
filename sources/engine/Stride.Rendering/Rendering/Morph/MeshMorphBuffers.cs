// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Graphics;

namespace Stride.Rendering;

/// <summary>
/// GPU inputs of a <see cref="RenderMesh"/> whose morph targets are applied in the vertex shader (<c>TransformationMorph</c>).
/// </summary>
public sealed class MeshMorphBuffers
{
    public const int Enabled = 1, Dense = 2, Normal = 4, Tangent = 8;

    /// <summary>Packed <see cref="MeshMorphEntry"/> values, shared by every instance of the mesh.</summary>
    public Buffer Entries;

    /// <summary>First entry of each vertex (sparse layout), shared by every instance of the mesh.</summary>
    public Buffer VertexOffsets;

    /// <summary>One weight per morph target.</summary>
    public Buffer Weights;

    /// <summary>Combination of <see cref="Enabled"/>, <see cref="Dense"/>, <see cref="Normal"/> and <see cref="Tangent"/>; the shader permutation.</summary>
    public int Flags;

    /// <summary>Number of morph targets, the length of <see cref="Weights"/>.</summary>
    public int TargetCount;

    /// <summary>Number of vertices of the mesh.</summary>
    public int VertexCount;
}
