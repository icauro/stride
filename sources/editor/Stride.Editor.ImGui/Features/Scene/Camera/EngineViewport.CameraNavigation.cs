using Stride.Core.Mathematics;
using Stride.Editor.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal float? OrthographicNavigationSceneRadius()
    {
        if (!Camera.Orthographic || loadedScene == null) return null;
        var bounds = BoundingSphere.Empty;
        // The native ContentScene excludes editor cameras, gizmos and prefab
        // lighting. Use actual authored runtime roots, with the same original
        // recursive mesh/skeleton bounds helper used by native framing.
        foreach (var entity in runtimeEntities.Values)
            if (entity.Scene == loadedScene && entity.Transform.Parent == null)
                bounds = BoundingSphere.Merge(bounds, entity.CalculateBoundSphere());
        foreach (var child in childScenes.Values)
            foreach (var entity in child.Entities.Values)
                if (entity.Scene == child.Scene && entity.Transform.Parent == null)
                    bounds = BoundingSphere.Merge(bounds, entity.CalculateBoundSphere());
        return bounds.Radius;
    }
}
