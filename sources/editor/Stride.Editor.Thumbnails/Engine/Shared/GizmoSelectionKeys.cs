using Stride.Core;

namespace Stride.Engine.Gizmos;

public static class GizmoSelectionKeys
{
    public static readonly PropertyKey<bool> Selected = new("SelectedProperty", typeof(GizmoSelectionKeys));
}
