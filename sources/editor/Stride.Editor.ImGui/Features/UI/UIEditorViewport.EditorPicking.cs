using Stride.Core.Mathematics;
using Stride.Rendering.UI;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorViewport
{
    internal void InvalidateRuntimeLayout() => observedVersion = -1;
    internal IReadOnlyList<Guid> OrderedAdornmentElements(IEnumerable<Guid> selection)
    {
        var ids = selection.ToHashSet();
        // Native AdornerLayer puts its canvas at the element's UI-space position,
        // offset in front by DepthBias + 1, then TouchDown orders intersections by Z.
        return runtime.Values.Where(element => ids.Contains(element.Id))
            .OrderBy(element => element.WorldMatrix.TranslationVector.Z - (element.DepthBias + 1))
            .Select(element => element.Id).ToArray();
    }
    private Matrix RenderWorldViewProjection()
    {
        // UIRenderFeature.UiElementState.Update: the preview is neither fullscreen nor billboard.
        var world = Matrix.Scaling(component.Size / component.Resolution) * uiEntity.Transform.WorldMatrix;
        world.Row2 = -world.Row2;
        world.Row3 = -world.Row3;
        return world * camera.ViewMatrix * camera.ProjectionMatrix;
    }

    private Matrix PickingWorldViewProjection()
    {
        // PickingUpdate reverses Z again to match UIElement.WorldMatrixPickingInternal.
        var matrix = RenderWorldViewProjection();
        matrix.Row3 = -matrix.Row3;
        return matrix;
    }

    internal bool TryCanvasPosition(NVector2 point, NVector2 displaySize, out Vector3 position)
    {
        position = default;
        if (disposed || target == null || RenderError != null || displaySize.X <= 0 || displaySize.Y <= 0) return false;
        var matrix = PickingWorldViewProjection();
        var determinant = matrix.Determinant();
        if (!float.IsFinite(determinant) || determinant == 0) return false;
        var inverse = Matrix.Invert(matrix);
        var x = point.X * 2 / displaySize.X - 1; var y = 1 - point.Y * 2 / displaySize.Y;
        var near = Vector3.TransformCoordinate(new Vector3(x, y, 0), inverse);
        var far = Vector3.TransformCoordinate(new Vector3(x, y, 1), inverse);
        var direction = far - near;
        if (!float.IsFinite(direction.Z) || Math.Abs(direction.Z) < MathUtil.ZeroTolerance) return false;
        position = near + direction * (-near.Z / direction.Z);
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
    }

    internal IReadOnlyList<Guid> ElementsAtPosition(NVector2 point, NVector2 displaySize)
    {
        if (disposed || target == null || RenderError != null || component.Page?.RootElement is not { } root
            || displaySize.X <= 0 || displaySize.Y <= 0 || point.X < 0 || point.Y < 0 || point.X > displaySize.X || point.Y > displaySize.Y) return [];
        var worldViewProjection = PickingWorldViewProjection();
        var determinant = worldViewProjection.Determinant();
        if (!float.IsFinite(determinant) || determinant == 0) return [];
        var inverse = Matrix.Invert(worldViewProjection);
        var x = point.X * 2 / displaySize.X - 1; var y = 1 - point.Y * 2 / displaySize.Y;
        var near = Vector3.TransformCoordinate(new Vector3(x, y, 0), inverse);
        var far = Vector3.TransformCoordinate(new Vector3(x, y, 1), inverse);
        var direction = far - near;
        if (!float.IsFinite(direction.LengthSquared()) || direction.LengthSquared() < MathUtil.ZeroTolerance) return [];
        direction.Normalize();
        var ray = new Ray(near, direction);
        // Actual virtual Intersects/visibility/clip/layout policy, including noninteractive editor containers.
        return UIRenderFeature.GetElementsAtPositionForEditor(root, ref ray, ref worldViewProjection)
            .Where(hit => runtime.ContainsKey(hit.Element.Id))
            .OrderBy(hit => Vector3.TransformCoordinate(hit.IntersectionPoint, worldViewProjection).Z)
            .ThenByDescending(hit => hit.DepthBias)
            .Select(hit => hit.Element.Id).Distinct().ToArray();
    }
}
