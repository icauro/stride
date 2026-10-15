using Hexa.NET.ImGui;
using Stride.UI.Controls;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private sealed record CanvasPress(UIDocument Source, UIHierarchyDragOwner Owner, long Revision, long Layout, NVector2 Origin);
    private CanvasPress? canvasPress;
    private bool canvasPressTransferred;

    private void CancelCanvasPointer()
    {
        var press = canvasPress; canvasPress = null;
        press?.Owner.Dispose(); canvasPressTransferred = false;
    }

    private void BeginCanvasPointer(UIDocument source, NVector2 point, NVector2 size)
    {
        CancelCanvasPointer();
        var owner = host.AcquireHierarchyCommandOwner(source, false);
        if (owner == null) return;
        try
        {
            StopLayoutDrag();
            host.ValidateHierarchyEdits(); source.CommitEdits();
            if (!owner.IsCurrent || !ReferenceEquals(owner.State.Document, source)) return;
            canvasPress = new(source, owner, source.EditRevision, source.LayoutVersion, Gui.GetIO().MousePos);
            owner = null;
            moving = null; resizing = 0;
            // Native TouchDown only prepares adorners of elements already selected.
            if (SelectedAdornmentAt(point, size) is { } hit)
            { moving = hit.Id; resizing = hit.Direction == Stride.Assets.Presentation.ViewModel.ResizingDirection.Center ? 0 : hit.Direction; }
            if (!host.CanEditHierarchyOwner(source)) moving = null;
            BeginLayoutPointer(point, size);
            if (Gui.GetIO().KeyAlt && selected.Count == 1 && moving is { } dragged)
            {
                canvasPressTransferred = BeginCanvasHierarchyDrag(source, dragged);
                moving = null; resizing = 0;
            }
        }
        catch (Exception error)
        {
            CancelCanvasPointer(); StopLayoutDrag();
            host.ReportValidationFailure(error);
        }
        finally { owner?.Dispose(); }
    }

    private void CompleteCanvasPointer(UIDocument source, NVector2 point, bool hovered)
    {
        var press = canvasPress;
        if (press == null) return;
        if (!Gui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!Gui.IsMouseDown(ImGuiMouseButton.Left) || Gui.IsKeyPressed(ImGuiKey.Escape, false))
            { CancelCanvasPointer(); StopLayoutDrag(); }
            return;
        }
        try
        {
            if (canvasPressTransferred || !hovered || !press.Owner.IsCurrent
                || !ReferenceEquals(press.Source, source) || source.EditRevision != press.Revision || source.LayoutVersion != press.Layout) return;
            if (layoutDragStarted)
            {
                if (layoutRuntimeChanged && moving is { } movingId && viewport!.FindRuntime(movingId) is { } runtime
                    && host.CanEditHierarchyOwner(source))
                {
                    host.ValidateHierarchyEdits();
                    if (!press.Owner.IsCurrent || source.EditRevision != press.Revision || source.LayoutVersion != press.Layout) return;
                    source.BeginEdit(resizing == 0 ? "Move UI element" : "Resize UI element");
                    try { source.CommitRuntimeLayout(movingId, runtime); }
                    finally { source.EndEdit(); }
                }
                return;
            }
            if (viewport!.Pick(point) is not { } id)
            { selected.Clear(); RefreshSelection(); return; }
            if (WindowsSourceTextDragDrop.IsBelowThreshold(Gui.GetIO().MousePos - press.Origin))
                Select(id, Gui.GetIO().KeyCtrl);
        }
        catch (Exception error) { host.ReportValidationFailure(error); }
        finally { CancelCanvasPointer(); }
    }
}
