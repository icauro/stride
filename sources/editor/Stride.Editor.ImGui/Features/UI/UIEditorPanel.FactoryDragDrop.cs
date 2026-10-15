using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class UIEditorPanel
{
    private const string UIFactoryPayload = "STRIDE_UI_FACTORY";
    private sealed record FactoryDrag(Guid Token, UIElementFactory Factory, UIHierarchyDragOwner Owner, long Revision, long Layout);
    private sealed class FactoryDrop(FactoryDrag drag, UIDocument destination, UIHierarchyDragOwner libraryOwner,
        UIHierarchyDragOwner destinationOwner, long revision, long layout, Guid? parent, int index) : IDisposable
    {
        internal FactoryDrag Drag { get; } = drag;
        internal UIDocument Destination { get; } = destination;
        internal UIHierarchyDragOwner LibraryOwner { get; } = libraryOwner;
        internal UIHierarchyDragOwner DestinationOwner { get; } = destinationOwner;
        internal long Revision { get; } = revision;
        internal long Layout { get; } = layout;
        internal Guid? Parent { get; } = parent;
        internal int Index { get; } = index;
        public void Dispose() { DestinationOwner.Dispose(); LibraryOwner.Dispose(); }
    }
    private FactoryDrag? factoryDrag;
    private FactoryDrop? factoryDrop;
    private (AssetId Asset, Guid Element)? selectedFactory;
    private float factoryPaletteRatio = .5f;

    private void BeginFactoryFrame()
    {
        var payload = Gui.GetDragDropPayload();
        if (factoryDrag is { } drag && (!Gui.GetCurrentContext().DragDropActive || payload.IsNull
            || !payload.IsDataType(UIFactoryPayload) || !IsCurrentFactory(drag, drag.Owner))) ClearFactoryDrag();
    }
    private bool IsCurrentFactory(FactoryDrag drag, UIHierarchyDragOwner owner)
        => owner.IsCurrent && host.IsCurrentFactoryDefinition(drag.Factory)
            && drag.Factory.Library.EditRevision == drag.Revision && drag.Factory.Library.LayoutVersion == drag.Layout
            && drag.Factory.Library.Asset is Stride.UI.Assets.UILibraryAsset library
            && library.PublicUIElements.ContainsKey(drag.Factory.ElementId)
            && library.Hierarchy.RootParts.Any(root => root.Id == drag.Factory.ElementId);
    private void ClearFactoryDrag()
    {
        var drag = factoryDrag; factoryDrag = null;
        if (Gui.GetCurrentContext().Handle != null)
        {
            var payload = Gui.GetDragDropPayload();
            if (!payload.IsNull && payload.IsDataType(UIFactoryPayload)) ImGuiP.ClearDragDrop();
        }
        drag?.Owner.Dispose();
    }
    private void ClearFactoryTransfers()
    {
        ClearFactoryDrag(); var drop = factoryDrop; factoryDrop = null; drop?.Dispose(); selectedFactory = null;
    }
    private void DrawFactoryPalette(UIDocument destination)
    {
        var available = Gui.GetContentRegionAvail().Y;
        var minimumPane = Gui.GetFrameHeightWithSpacing() * 2;
        var splitterHeight = 5 * Gui.GetStyle().FontScaleDpi;
        if (available <= minimumPane * 2 + splitterHeight) return;
        var factories = host.Factories.Where(factory => factory.SystemLibrary || factory.Library.Asset.Id != destination.Asset.Id).ToArray();
        if (selectedFactory is { } previous && !factories.Any(factory => factory.Library.Asset.Id == previous.Asset && factory.ElementId == previous.Element))
            selectedFactory = null;
        var height = Math.Clamp(available * factoryPaletteRatio, minimumPane, available - minimumPane - splitterHeight);
        if (Gui.BeginChild("##UILibraryPalette", new Vector2(0, height), ImGuiChildFlags.Borders))
        {
            host.DrawViewLabel("UI library", true);
            foreach (var system in new[] { true, false })
            {
                var entries = factories.Where(factory => factory.SystemLibrary == system).ToArray();
                if (entries.Length == 0) continue;
                var label = system ? "Standard library" : "User libraries";
                if (!host.DrawFactoryCategoryGroup(system ? "##StandardFactoryGroup" : "##UserFactoryGroup", label)) continue;
                try
                {
                    foreach (var category in entries.GroupBy(factory => factory.Category).OrderBy(group => group.Key, StringComparer.CurrentCulture))
                    {
                        var identity = "##UIFactoryCategory/" + Uri.EscapeDataString(category.Key);
                        if (!host.DrawFactoryCategoryGroup(identity, category.Key)) continue;
                        try
                        {
                            foreach (var factory in category.OrderBy(factory => factory.Name, StringComparer.CurrentCulture))
                            {
                                Gui.PushID(factory.Library.Asset.Id.ToString()); Gui.PushID(factory.ElementId.ToString());
                                try
                                {
                                    var key = (factory.Library.Asset.Id, factory.ElementId);
                                    if (host.DrawFactoryRow(factory.Name + "###UIFactory", selectedFactory == key)) selectedFactory = key;
                                    host.RegisterCaptureItem("ui.factory." + key.Item1 + "." + key.Item2, "listitem", factory.Name);
                                    DrawFactorySource(factory);
                                }
                                finally { Gui.PopID(); Gui.PopID(); }
                            }
                        }
                        finally { Gui.TreePop(); }
                    }
                }
                finally { Gui.TreePop(); }
            }
        }
        ImGuiScopes.EndChild();
        host.RegisterCaptureItem("ui.factory-palette", "section", "UI library");
        Gui.InvisibleButton("##UILibrarySplitter", new Vector2(Gui.GetContentRegionAvail().X, splitterHeight));
        if (Gui.IsItemHovered() || Gui.IsItemActive()) Gui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (Gui.IsItemActive()) factoryPaletteRatio = Math.Clamp((height + Gui.GetIO().MouseDelta.Y) / available, minimumPane / available,
            (available - minimumPane - splitterHeight) / available);
    }
    private void DrawFactorySource(UIElementFactory factory)
    {
        var row = Gui.GetCurrentContext().LastItemData;
        if (!Gui.BeginDragDropSource()) return;
        try
        {
            if (factoryDrag == null)
            {
                host.ValidateHierarchyEdits(); factory.Library.CommitEdits();
                var owner = host.AcquireFactoryLibraryOwner(factory);
                if (owner == null) return;
                try { factoryDrag = new(Guid.NewGuid(), factory, owner, factory.Library.EditRevision, factory.Library.LayoutVersion); }
                catch { owner.Dispose(); throw; }
            }
            if (!IsCurrentFactory(factoryDrag, factoryDrag.Owner)) return;
            var token = factoryDrag.Token;
            Gui.SetDragDropPayload(UIFactoryPayload, &token, (nuint)sizeof(Guid));
            host.DrawHierarchyDragText(factoryDrag.Factory.Name);
        }
        catch (Exception error) { host.ReportValidationFailure(error); }
        finally { Gui.EndDragDropSource(); Gui.GetCurrentContext().LastItemData = row; }
    }
    private void DrawFactoryDropTarget(UIDocument destination, Guid? parent, int index, Vector2 minimum, Vector2 maximum,
        bool insert, Vector2 line, uint identity, Vector2? highlightMinimum = null, Vector2? highlightMaximum = null)
    {
        if (factoryDrag is not { } drag || !ImGuiP.BeginDragDropTargetCustom(new ImRect(minimum, maximum), identity)) return;
        try
        {
            if (!IsCurrentFactory(drag, drag.Owner) || !host.CanEditHierarchyOwner(destination)
                || !destination.CanInstantiateFactory(drag.Factory, parent, index, host.ResolveLibrary))
            { Gui.SetMouseCursor(ImGuiMouseCursor.NotAllowed); return; }
            var payload = Gui.AcceptDragDropPayload(UIFactoryPayload, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (payload.IsNull || payload.DataSize != sizeof(Guid) || *(Guid*)payload.Data != drag.Token) return;
            if (payload.IsPreview())
            {
                var draw = Gui.GetWindowDrawList(); var color = Gui.GetColorU32(ImGuiCol.DragDropTarget);
                if (insert) draw.AddLine(line, new Vector2(maximum.X, line.Y), color, 2 * Gui.GetStyle().FontScaleDpi);
                else draw.AddRect(highlightMinimum ?? minimum, highlightMaximum ?? maximum, color);
            }
            if (!payload.IsDelivery()) return;
            UIHierarchyDragOwner? libraryOwner = null, destinationOwner = null;
            try
            {
                libraryOwner = host.AcquireFactoryLibraryOwner(drag.Factory);
                destinationOwner = host.AcquireHierarchyDragOwner(destination);
                if (libraryOwner == null || destinationOwner == null) return;
                var prepared = new FactoryDrop(drag, destination, libraryOwner, destinationOwner, destination.EditRevision, destination.LayoutVersion, parent, index);
                factoryDrop?.Dispose(); factoryDrop = prepared;
                libraryOwner = destinationOwner = null;
            }
            finally { destinationOwner?.Dispose(); libraryOwner?.Dispose(); }
        }
        catch (Exception error) { host.ReportValidationFailure(error); }
        finally { Gui.EndDragDropTarget(); }
    }
    private void DrawFactoryCanvasTarget(UIDocument destination, Vector2 origin, Vector2 size)
    {
        if (factoryDrag is not { } drag || viewport == null || !Gui.IsMouseHoveringRect(origin, origin + size)) return;
        var target = viewport.ElementsAtPosition(Gui.GetIO().MousePos - origin, size)
            .Select(destination.Find).FirstOrDefault(element => element != null
                && destination.CanInstantiateFactory(drag.Factory, element.Id, element.VisualChildren.Count, host.ResolveLibrary));
        if (target == null) { Gui.SetMouseCursor(ImGuiMouseCursor.NotAllowed); return; }
        var minimum = origin; var maximum = origin + size;
        if (viewport.TryElementRect(target.Id, out var targetMinimum, out var targetMaximum))
        { minimum = origin + targetMinimum; maximum = origin + targetMaximum; }
        // The actual canvas owns hit testing; highlight the actual accepted element bounds.
        DrawFactoryDropTarget(destination, target.Id, target.VisualChildren.Count, origin, origin + size, false, minimum,
            Gui.GetID("##UIFactoryCanvasDrop"), minimum, maximum);
    }
    private void ProcessFactoryDrop()
    {
        var drop = factoryDrop; factoryDrop = null;
        if (drop == null) return;
        using (drop)
        {
            try
            {
                host.ValidateHierarchyEdits();
                if (!IsCurrentFactory(drop.Drag, drop.LibraryOwner) || !drop.DestinationOwner.IsCurrent
                    || !host.CanEditHierarchyOwner(drop.Destination) || !ReferenceEquals(document, drop.Destination)
                    || drop.Destination.EditRevision != drop.Revision || drop.Destination.LayoutVersion != drop.Layout) return;
                var id = drop.Destination.InstantiateFactory(drop.Drag.Factory, drop.Parent, drop.Index, host.ResolveLibrary);
                Select(id, false);
            }
            catch (Exception error) { host.ReportValidationFailure(error); }
        }
    }
}
