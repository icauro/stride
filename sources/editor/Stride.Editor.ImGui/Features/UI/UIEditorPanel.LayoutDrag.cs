using Hexa.NET.ImGui;
using Stride.UI.Editor.Game;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private float snapValue { get => ViewSettings.SnapValue; set => ViewSettings.SnapValue = value; }
    private float dragSnapValue;
    private Vector3 layoutPointerPosition;
    private bool layoutPointerCaptured, layoutDragStarted;
    private bool layoutRuntimeChanged;
    private NVector2 layoutPointerScreenOrigin;

    private void DrawLayoutSnap()
    {
        Gui.SetNextItemWidth(50);
        var value = snapValue;
        if (Gui.InputFloat("##UILayoutSnap", ref value, 0, 0, "%.3f") && float.IsFinite(value))
            snapValue = Math.Max(0, value);
        if (Gui.IsItemHovered()) Gui.SetTooltip("Snaps to this value (0 to disable)");
    }

    private void BeginLayoutPointer(NVector2 point, NVector2 size)
    {
        layoutPointerScreenOrigin = Gui.GetIO().MousePos;
        layoutDragStarted = false;
        layoutRuntimeChanged = false;
        layoutPointerCaptured = viewport != null && viewport.TryCanvasPosition(point, size, out layoutPointerPosition);
    }

    private static Vector3 SnapLayoutPosition(Vector3 position, float interval)
    {
        if (interval > float.Epsilon)
        {
            position.X = MathF.Round(position.X / interval) * interval;
            position.Y = MathF.Round(position.Y / interval) * interval;
        }
        return position;
    }

    private void UpdateLayoutDrag(UIDocument source, Guid id, NVector2 point, NVector2 size)
    {
        if (!host.CanEditHierarchyOwner(source)) { CancelCanvasPointer(); StopLayoutDrag(); return; }
        if (canvasPress is not { } press || !press.Owner.IsCurrent || !ReferenceEquals(press.Source, source)
            || source.EditRevision != press.Revision || source.LayoutVersion != press.Layout)
        { CancelCanvasPointer(); StopLayoutDrag(); return; }
        if (!layoutPointerCaptured || viewport == null || !source.CanSave
            || viewport.FindRuntime(id) is not { } element || !viewport.TryCanvasPosition(point, size, out var position)) return;
        if (!layoutDragStarted)
        {
            if (!WindowsSourceTextDragDrop.ExceedsThreshold(Gui.GetIO().MousePos - layoutPointerScreenOrigin)) return;
            dragSnapValue = snapValue;
            layoutPointerPosition = SnapLayoutPosition(layoutPointerPosition, dragSnapValue);
            layoutDragStarted = true;
        }
        position = SnapLayoutPosition(position, dragSnapValue);
        var delta = position - layoutPointerPosition;
        delta.Z = 0;
        if (delta.X == 0 && delta.Y == 0) return;
        var resolution = source.Asset.Design.Resolution;
        const float magnetDistance = 10;
        var changed = resizing == 0
            ? UILayoutHelper.Move(element, ref delta, magnetDistance, ref resolution)
            : UILayoutHelper.Resize(element, resizing, ref delta, magnetDistance, ref resolution);
        if (!changed) return;
        layoutRuntimeChanged = true;
        layoutPointerPosition += delta;
    }

    private void StopLayoutDrag()
    {
        if (layoutRuntimeChanged) viewport?.InvalidateRuntimeLayout();
        moving = null;
        layoutPointerCaptured = layoutDragStarted = layoutRuntimeChanged = false;
        document?.EndEdit();
    }
}
