using Stride.Core.Mathematics;

namespace Stride.Engine.Design;

/// <summary>Same two-float mesh/component wire format used by WPF picking shaders.</summary>
internal struct EditorPickingData
{
    public float ModelComponentId;
    public float MeshMaterialIndex;
    public EditorPickingData(int componentId, int meshIndex, int materialIndex)
    {
        ModelComponentId = componentId;
        MeshMaterialIndex = meshIndex + MathUtil.Clamp(materialIndex, 0, 1023) / 1024f;
    }
}
