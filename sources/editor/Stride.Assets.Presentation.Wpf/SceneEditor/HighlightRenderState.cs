using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Assets.Presentation.SceneEditor;

/// <summary>Actual highlight memberships owned by one editor compositor.</summary>
public sealed class HighlightRenderState
{
    public readonly Dictionary<Material, Color4> MaterialHighlightColors = new();
    public readonly Dictionary<Mesh, Color4> MeshHighlightColors = new();
    public readonly Dictionary<ModelComponent, Color4> ModelHighlightColors = new();
    public readonly HashSet<Material> MaterialsHighlightedForModel = new();
    public void Clear()
    {
        MaterialHighlightColors.Clear(); MeshHighlightColors.Clear();
        ModelHighlightColors.Clear(); MaterialsHighlightedForModel.Clear();
    }
}
