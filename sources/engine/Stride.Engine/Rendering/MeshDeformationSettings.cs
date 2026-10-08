// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Data;

namespace Stride.Rendering;

/// <summary>Where morph targets and skinning of <see cref="Engine.ModelComponent"/> meshes are evaluated.</summary>
[DataContract]
public enum MeshDeformationMode
{
    /// <summary>Morphs and skinning in the vertex shader of every pass; no extra vertex memory.</summary>
    VertexShader,
    /// <summary>Morphs and skinning in compute once per frame; every pass reads the deformed vertices.</summary>
    Compute,
}

/// <summary>
/// Game-wide mesh deformation settings. <see cref="ModelRenderProcessor"/> reads this instance every frame,
/// so changing it at runtime (for example from a quality menu) applies immediately.
/// </summary>
[DataContract]
[Display("Mesh deformation")]
public sealed class MeshDeformationSettings : Configuration
{
    /// <userdoc>Where morph targets and skinning are evaluated.</userdoc>
    [DataMember(10)]
    [DefaultValue(MeshDeformationMode.VertexShader)]
    public MeshDeformationMode Mode { get; set; } = MeshDeformationMode.VertexShader;

    /// <userdoc>Maximum number of instances of one mesh deformed by a single compute dispatch. 1 gives one dispatch per instance.</userdoc>
    [DataMember(30)]
    [DataMemberRange(1, 1024, 1, 8, 0)]
    [DefaultValue(32)]
    public int BatchSize { get; set; } = 32;

    /// <userdoc>Thread group shape of deformation dispatches (vertices x instances).</userdoc>
    [DataMember(40)]
    [DefaultValue(DeformationThreadGroup.X32Y16)]
    public DeformationThreadGroup ThreadGroup { get; set; } = DeformationThreadGroup.X32Y16;
}
