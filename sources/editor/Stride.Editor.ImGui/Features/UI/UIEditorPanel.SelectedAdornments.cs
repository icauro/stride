using Hexa.NET.ImGui;
using Stride.Assets.Presentation.ViewModel;
using Stride.UI.Controls;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private (Guid Id, ResizingDirection Direction)? SelectedAdornmentAt(NVector2 point, NVector2 size)
    {
        if (viewport == null || document == null || !host.CanEditHierarchyOwner(document)) return null;
        var bodies = viewport.ElementsAtPosition(point, size).ToHashSet();
        foreach (var id in viewport.OrderedAdornmentElements(selected))
        {
            if (!viewport.TryElementRect(id, out var minimum, out var maximum)) continue;
            foreach (var handle in Handles(minimum, maximum))
                if (Math.Abs(point.X - handle.Point.X) <= SizingHandleHalfSize && Math.Abs(point.Y - handle.Point.Y) <= SizingHandleHalfSize)
                    return (id, handle.Direction);
            var border = ViewSettings.Selection.Thickness * viewport.Zoom;
            if (bodies.Contains(id) || point.X >= minimum.X - border && point.X <= maximum.X + border
                && point.Y >= minimum.Y - border && point.Y <= maximum.Y + border)
                return (id, ResizingDirection.Center);
        }
        return null;
    }

    private void DrawAdornmentCursor(NVector2 point, NVector2 size, bool hovered)
    {
        if (factoryDrag != null || canvasHierarchyDrag || cameraPanning || !hovered && !layoutDragStarted) return;
        (Guid Id, ResizingDirection Direction)? target = layoutDragStarted && moving is { } id ? (Id: id, Direction: resizing == 0 ? ResizingDirection.Center : resizing)
            : SelectedAdornmentAt(point, size);
        if (target is not { } hit) return;
        var cursor = hit.Direction switch
        {
            ResizingDirection.Center => document!.Find(hit.Id)?.VisualParent is ContentControl ? ImGuiMouseCursor.NotAllowed : ImGuiMouseCursor.ResizeAll,
            ResizingDirection.Top or ResizingDirection.Bottom => ImGuiMouseCursor.ResizeNs,
            ResizingDirection.Left or ResizingDirection.Right => ImGuiMouseCursor.ResizeEw,
            ResizingDirection.TopLeft or ResizingDirection.BottomRight => ImGuiMouseCursor.ResizeNwse,
            ResizingDirection.TopRight or ResizingDirection.BottomLeft => ImGuiMouseCursor.ResizeNesw,
            _ => ImGuiMouseCursor.Arrow,
        };
        Gui.SetMouseCursor(cursor);
    }
}
