using System.Numerics;
using Hexa.NET.ImGui;
using Stride.UI;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class UIEditorPanel
{
    private const string UIHierarchyPayload = "STRIDE_UI_HIERARCHY";
    private sealed record HierarchyDrag(Guid Token, UIDocument Source, UIHierarchyDragOwner Owner, long Revision, long Layout,
        Guid[] Roots, Guid[] Selection);
    private sealed class HierarchyDrop(HierarchyDrag drag, UIDocument destination, UIHierarchyDragOwner sourceOwner,
        UIHierarchyDragOwner destinationOwner, long revision, long layout, Guid? parent, int index) : IDisposable
    {
        internal HierarchyDrag Drag { get; } = drag;
        internal UIDocument Destination { get; } = destination;
        internal UIHierarchyDragOwner SourceOwner { get; } = sourceOwner;
        internal UIHierarchyDragOwner DestinationOwner { get; } = destinationOwner;
        internal long Revision { get; } = revision;
        internal long Layout { get; } = layout;
        internal Guid? Parent { get; } = parent;
        internal int Index { get; } = index;
        public void Dispose() { DestinationOwner.Dispose(); SourceOwner.Dispose(); }
    }
    private HierarchyDrag? hierarchyDrag;
    private bool canvasHierarchyDrag;
    private HierarchyDrop? hierarchyDrop;
    private readonly Dictionary<Guid, (Vector2 Minimum, Vector2 Maximum)> hierarchyRowBounds = [];

    private void BeginHierarchyFrame()
    {
        hierarchyRowBounds.Clear();
        BeginFactoryFrame();
        var payload = Gui.GetDragDropPayload();
        if (hierarchyDrag != null && (!Gui.GetCurrentContext().DragDropActive || payload.IsNull || !payload.IsDataType(UIHierarchyPayload)
            || !IsCurrentHierarchyDrag(hierarchyDrag))) ClearHierarchyDrag();
    }
    private bool IsCurrentHierarchyDrag(HierarchyDrag drag) => drag.Owner.IsCurrent && host.CanEditHierarchyOwner(drag.Source)
        && drag.Source.EditRevision == drag.Revision && drag.Source.LayoutVersion == drag.Layout
        && drag.Roots.All(id => drag.Source.Find(id) != null);
    private void ClearHierarchyDrag()
    {
        canvasHierarchyDrag = false;
        var drag = hierarchyDrag; hierarchyDrag = null;
        if (Gui.GetCurrentContext().Handle != null)
        {
            var payload = Gui.GetDragDropPayload();
            if (!payload.IsNull && payload.IsDataType(UIHierarchyPayload)) ImGuiP.ClearDragDrop();
        }
        drag?.Owner.Dispose();
    }
    private bool BeginCanvasHierarchyDrag(UIDocument source, Guid id)
    {
        if (hierarchyDrag != null || !host.CanEditHierarchyOwner(source) || source.Find(id) == null) return false;
        var owner = host.AcquireHierarchyDragOwner(source);
        if (owner == null) return false;
        try
        {
            if (!owner.IsCurrent || !ReferenceEquals(owner.State.Document, source)) return false;
            hierarchyDrag = new(Guid.NewGuid(), source, owner, source.EditRevision, source.LayoutVersion, [id], [id]);
            canvasHierarchyDrag = true; owner = null;
            return true;
        }
        finally { owner?.Dispose(); }
    }
    private void DrawCanvasHierarchyDragSource()
    {
        if (!canvasHierarchyDrag || hierarchyDrag is not { } drag) return;
        if (!IsCurrentHierarchyDrag(drag) || Gui.IsKeyPressed(ImGuiKey.Escape, false)) { ClearHierarchyDrag(); return; }
        // Original Alt gesture explicitly starts an editor element transfer on press.
        // SourceExtern keeps its actual payload independent of the canvas item/nav ID.
        var row = Gui.GetCurrentContext().LastItemData;
        if (!Gui.BeginDragDropSource(ImGuiDragDropFlags.SourceExtern | ImGuiDragDropFlags.PayloadAutoExpire)) return;
        try
        {
            var token = drag.Token;
            Gui.SetDragDropPayload(UIHierarchyPayload, &token, (nuint)sizeof(Guid));
            foreach (var id in drag.Roots)
            {
                var current = drag.Source.Find(id)!;
                host.DrawHierarchyDragText(string.IsNullOrEmpty(current.Name) ? current.GetType().Name : current.Name);
            }
        }
        finally { Gui.EndDragDropSource(); Gui.GetCurrentContext().LastItemData = row; }
    }
    private void ClearHierarchyTransfers()
    {
        ClearFactoryTransfers();
        ClearHierarchyDrag(); var drop = hierarchyDrop; hierarchyDrop = null; drop?.Dispose(); hierarchyRowBounds.Clear();
    }
    private void DrawHierarchyDragSource(UIDocument source, UIElement element)
    {
        if (!host.CanEditHierarchyOwner(source)) return;
        var row = Gui.GetCurrentContext().LastItemData;
        if (!Gui.BeginDragDropSource()) return;
        try
        {
            if (hierarchyDrag == null)
            {
                host.ValidateHierarchyEdits(); source.CommitEdits();
                var selection = selected.Contains(element.Id) ? selected.ToArray() : [element.Id];
                var roots = source.OrderedHierarchyRoots(selection);
                var owner = host.AcquireHierarchyDragOwner(source);
                if (owner == null || roots.Length == 0) { owner?.Dispose(); return; }
                try { hierarchyDrag = new(Guid.NewGuid(), source, owner, source.EditRevision, source.LayoutVersion, roots, selection); }
                catch { owner.Dispose(); throw; }
            }
            if (!IsCurrentHierarchyDrag(hierarchyDrag)) return;
            var token = hierarchyDrag.Token;
            Gui.SetDragDropPayload(UIHierarchyPayload, &token, (nuint)sizeof(Guid));
            foreach (var id in hierarchyDrag.Roots)
            {
                var current = hierarchyDrag.Source.Find(id)!;
                host.DrawHierarchyDragText(string.IsNullOrEmpty(current.Name) ? current.GetType().Name : current.Name);
            }
        }
        catch (Exception error) { host.ReportValidationFailure(error); }
        finally { Gui.EndDragDropSource(); Gui.GetCurrentContext().LastItemData = row; }
    }
    private UIElement ResolveHierarchyAfterTarget(UIElement element, float pointerX)
    {
        var target = element; var child = element;
        for (var parent = child.VisualParent; parent != null && ReferenceEquals(parent.VisualChildren.LastOrDefault(), child); parent = child.VisualParent)
        {
            if (hierarchyRowBounds.TryGetValue(target.Id, out var bounds) && pointerX < bounds.Minimum.X) target = parent;
            child = parent;
        }
        return target;
    }
    private static float HierarchyInsertBand(float height)
    {
        // Same DIP-scaled policy as the reference DropZoneCalculator.
        var dpi = Gui.GetStyle().FontScaleDpi;
        return Math.Min(Math.Clamp(height * .3f, 3 * dpi, 10 * dpi), height * .4f);
    }
    private void DrawHierarchyRowDrop(UIDocument destination, UIElement element, bool open, Vector2 minimum, Vector2 maximum)
    {
        if (hierarchyDrag == null && factoryDrag == null || !Gui.IsMouseHoveringRect(minimum, maximum)) return;
        var pointer = Gui.GetIO().MousePos;
        var band = HierarchyInsertBand(maximum.Y - minimum.Y);
        var before = pointer.Y < minimum.Y + band;
        var after = !before && !(open && element.VisualChildren.Count != 0) && pointer.Y > maximum.Y - band;
        var anchor = after ? ResolveHierarchyAfterTarget(element, pointer.X) : element;
        var parent = before || after ? anchor.VisualParent : element;
        var index = before || after ? destination.Asset.IndexOf(anchor) + (after ? 1 : 0) : parent.VisualChildren.Count;
        var lineX = after && hierarchyRowBounds.TryGetValue(anchor.Id, out var bounds) ? bounds.Minimum.X : minimum.X;
        DrawHierarchyDropTarget(destination, parent?.Id, index, minimum, maximum, before || after,
            new Vector2(lineX, before ? minimum.Y : maximum.Y), Gui.GetID("##UIHierarchyDrop"), before || after ? anchor.Id : null);
    }
    private void DrawHierarchyEmptyDrop(UIDocument destination)
    {
        var minimum = Gui.GetCursorScreenPos(); var maximum = ImGuiP.GetCurrentWindow().InnerClipRect.Max;
        minimum = Vector2.Max(minimum, ImGuiP.GetCurrentWindow().InnerClipRect.Min);
        if (maximum.X > minimum.X && maximum.Y > minimum.Y)
            DrawHierarchyDropTarget(destination, null, destination.Roots.Count, minimum, maximum, false, minimum, Gui.GetID("##UIHierarchyEmptyDrop"));
    }
    private void DrawHierarchyDropTarget(UIDocument destination, Guid? parent, int index, Vector2 minimum, Vector2 maximum,
        bool insert, Vector2 line, uint identity, Guid? anchor = null)
    {
        DrawFactoryDropTarget(destination, parent, index, minimum, maximum, insert, line, identity);
        if (hierarchyDrag is not { } drag || !ImGuiP.BeginDragDropTargetCustom(new ImRect(minimum, maximum), identity)) return;
        try
        {
            var valid = IsCurrentHierarchyDrag(drag) && host.CanEditHierarchyOwner(destination)
                && !(ReferenceEquals(destination, drag.Source) && anchor is { } selectedAnchor && drag.Roots.Contains(selectedAnchor))
                && destination.CanMoveHierarchyFrom(drag.Source, drag.Roots, parent, index)
                && (ReferenceEquals(destination, drag.Source) || !drag.Source.HasHierarchyLibraryDependency(destination.Asset.Id, drag.Roots, host.ResolveLibrary));
            if (!valid) { Gui.SetMouseCursor(ImGuiMouseCursor.NotAllowed); return; }
            var payload = Gui.AcceptDragDropPayload(UIHierarchyPayload, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (payload.IsNull || payload.DataSize != sizeof(Guid) || *(Guid*)payload.Data != drag.Token) return;
            if (payload.IsPreview())
            {
                var draw = Gui.GetWindowDrawList(); var color = Gui.GetColorU32(ImGuiCol.DragDropTarget);
                if (insert) draw.AddLine(line, new Vector2(maximum.X, line.Y), color, 2 * Gui.GetStyle().FontScaleDpi);
                else draw.AddRect(minimum, maximum, color);
            }
            if (!payload.IsDelivery()) return;
            UIHierarchyDragOwner? sourceOwner = null, destinationOwner = null;
            try
            {
                sourceOwner = host.AcquireHierarchyDragOwner(drag.Source);
                destinationOwner = host.AcquireHierarchyDragOwner(destination);
                if (sourceOwner == null || destinationOwner == null) return;
                var prepared = new HierarchyDrop(drag, destination, sourceOwner, destinationOwner, destination.EditRevision, destination.LayoutVersion, parent, index);
                hierarchyDrop?.Dispose(); hierarchyDrop = prepared;
                sourceOwner = destinationOwner = null;
            }
            finally { destinationOwner?.Dispose(); sourceOwner?.Dispose(); }
        }
        catch (Exception error) { host.ReportValidationFailure(error); }
        finally { Gui.EndDragDropTarget(); }
    }
    private void ProcessHierarchyDrop()
    {
        var drop = hierarchyDrop; hierarchyDrop = null;
        if (drop == null) return;
        using (drop)
        {
            try
            {
                host.ValidateHierarchyEdits();
                var drag = drop.Drag; var destination = drop.Destination;
                if (!drop.SourceOwner.IsCurrent || !drop.DestinationOwner.IsCurrent || !host.CanEditHierarchyOwner(drag.Source)
                    || !host.CanEditHierarchyOwner(destination) || !ReferenceEquals(document, destination)
                    || drag.Source.EditRevision != drag.Revision || drag.Source.LayoutVersion != drag.Layout
                    || destination.EditRevision != drop.Revision || destination.LayoutVersion != drop.Layout) return;
                var mapping = destination.MoveHierarchyFrom(drag.Source, drag.Roots, drop.Parent, drop.Index, host.ResolveLibrary);
                if (!ReferenceEquals(drag.Source, destination)) drag.Source.SelectExtensionElements([]);
                selected.Clear();
                foreach (var id in drag.Selection)
                {
                    var moved = mapping.TryGetValue(id, out var remapped) ? remapped : id;
                    if (destination.Find(moved) != null) selected.Add(moved);
                }
                RefreshSelection();
            }
            catch (Exception error) { host.ReportValidationFailure(error); }
        }
    }
}
