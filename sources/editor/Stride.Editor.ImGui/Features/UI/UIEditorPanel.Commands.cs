using Hexa.NET.ImGui;
using Stride.UI;
using Stride.UI.Panels;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private sealed record UICommand(UIDocument Source, UIHierarchyDragOwner Owner, long Revision, long Layout,
        Guid[] Ids, bool Writable, Action<UIDocument, Guid[]> Apply, FactoryDrag? Factory) : IDisposable
    { public void Dispose() { Factory?.Owner.Dispose(); Owner.Dispose(); } }
    private readonly Queue<UICommand> uiCommands = new();
    private void QueueUIPropertyCommand(UIDocument source, Guid[] ids, Action apply, bool writable = true)
    {
        // Captured presenter/collection targets precede CommitEdits. If that commit
        // changes their graph, never certify those old targets as the new revision.
        var revision = source.EditRevision; var layout = source.LayoutVersion;
        QueueUICommand(source, ids, writable, (asset, _) =>
        {
            if (asset.EditRevision != revision || asset.LayoutVersion != layout) return;
            apply();
        });
    }
    private bool focusElementRename;
    private Guid[] canvasContextHits = [];
    private bool QueueUICommand(UIDocument source, Guid[] ids, bool writable, Action<UIDocument, Guid[]> apply, UIElementFactory? factory = null)
    {
        var owner = host.AcquireHierarchyCommandOwner(source, writable);
        if (owner == null) return false;
        UIHierarchyDragOwner? factoryOwner = null;
        try
        {
            host.ValidateHierarchyEdits(); source.CommitEdits();
            if (!owner.IsCurrent) { owner.Dispose(); return false; }
            FactoryDrag? certificate = null;
            if (factory != null)
            {
                factory.Library.CommitEdits();
                factoryOwner = host.AcquireFactoryLibraryOwner(factory);
                if (factoryOwner == null) { owner.Dispose(); return false; }
                certificate = new(Guid.Empty, factory, factoryOwner, factory.Library.EditRevision, factory.Library.LayoutVersion);
            }
            uiCommands.Enqueue(new(source, owner, source.EditRevision, source.LayoutVersion, ids, writable, apply, certificate));
        }
        catch { factoryOwner?.Dispose(); owner.Dispose(); throw; }
        return true;
    }
    private void ProcessUICommands()
    {
        while (uiCommands.TryDequeue(out var command))
        {
            using (command)
            {
                try
                {
                    if (!command.Owner.IsCurrent || !ReferenceEquals(document, command.Source)
                        || command.Source.EditRevision != command.Revision || command.Source.LayoutVersion != command.Layout
                        || command.Ids.Any(id => command.Source.Find(id) == null)
                        || command.Factory is { } factory && !IsCurrentFactory(factory, factory.Owner)
                        || command.Writable && !host.CanEditHierarchyOwner(command.Source)) continue;
                    command.Apply(command.Source, command.Ids);
                }
                catch (Exception error) { host.ReportValidationFailure(error); }
            }
        }
    }
    private void ClearUICommands()
    {
        CancelUIElementDeletion();
        while (uiCommands.TryDequeue(out var command)) command.Dispose();
        canvasContextHits = []; focusElementRename = false;
    }
    private void SelectUIResult(IEnumerable<Guid> ids)
    {
        selected.Clear(); selected.UnionWith(ids); RefreshSelection();
    }
    private static bool AreUISiblings(UIDocument source, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0 || source.Find(ids[0]) is not { } first) return false;
        return ids.All(id => source.Find(id) is { } element && ReferenceEquals(element.VisualParent, first.VisualParent));
    }
    private void QueueUIFactoryInsertion(UIDocument source, UIElementFactory factory)
    {
        QueueUICommand(source, selected.ToArray(), true, (asset, ids) =>
        {
            var parent = ids.Length == 1 ? asset.Find(ids[0]) : null;
            var index = parent?.VisualChildren.Count ?? asset.Roots.Count;
            SelectUIResult([asset.InstantiateFactory(factory, parent?.Id, index, host.ResolveLibrary)]);
        }, factory);
    }
    private void QueueUIGrouping(UIDocument source, Guid[] ids, UIElementFactory factory)
    {
        QueueUICommand(source, ids, true, (asset, selection) =>
        {
            if (!factory.IsPanelFactory) throw new InvalidOperationException("The UI panel factory is no longer available.");
            if (!AreUISiblings(asset, selection)) throw new InvalidOperationException("Grouped UI elements must be siblings.");
            SelectUIResult([asset.GroupInto(selection, factory.Library, factory.ElementId, factory.Url, factory.SystemLibrary)]);
        }, factory);
    }
    internal bool ExecuteUICommand(string command)
    {
        if (document is not { } source) return false;
        var ids = selected.ToArray();
        switch (command)
        {
            case "delete" when ids.Length > 0:
                return QueueUICommand(source, ids, true, BeginUIElementDeletion);
            case "duplicate" when ids.Length > 0 && source.CommonRoots(ids).All(id => source.Find(id)!.VisualParent is Panel):
                return QueueUICommand(source, ids, true, (asset, selection) => SelectUIResult(asset.Duplicate(selection)));
            case "rename" when ids.Length == 1 && source.CanRenameElement(ids[0]):
                return QueueUICommand(source, ids, true, (asset, selection) => { asset.BeginElementRename(selection[0]); focusElementRename = true; });
            default: return ExecuteClipboardCommand(command);
        }
    }
    private void DrawInlineElementRename(UIDocument source, UIElement element, NVector2 minimum, NVector2 maximum)
    {
        if (source.RenamingElement != element.Id) return;
        var item = Gui.GetCurrentContext().LastItemData; var cursor = Gui.GetCursorScreenPos();
        try
        {
            Gui.SetCursorScreenPos(minimum + new NVector2(Gui.GetTreeNodeToLabelSpacing(), Math.Max(0, (maximum.Y - minimum.Y - Gui.GetFrameHeight()) / 2)));
            Gui.SetNextItemWidth(Math.Max(1, maximum.X - Gui.GetCursorScreenPos().X - Gui.GetStyle().FramePadding.X));
            if (focusElementRename) { Gui.SetKeyboardFocusHere(); focusElementRename = false; }
            var text = source.ElementRenameText;
            var submitted = NativeStringInput.Draw("##ElementName", ref text, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);
            host.RecordDiagnosticControl("ui/rename/" + element.Id);
            if (Gui.IsItemEdited()) source.UpdateElementRenameText(text);
            if (Gui.IsItemActive() && Gui.IsKeyPressed(ImGuiKey.Escape, false)) source.CancelElementRename();
            // An overlaid input can be reactivated in the same frame that its
            // previous navigation activation ends. Keep the draft while the
            // actual editor is still active, as the entity-tree rename does.
            else if (submitted || !Gui.IsItemActive() && Gui.IsItemDeactivated()) source.CommitElementRename();
        }
        finally
        {
            // Register the original tree-row baseline after the overlaid input.
            // Its trailing spacing can lie below the input's measured bounds;
            // restoring a cursor alone leaves that extent unregistered.
            Gui.SetCursorScreenPos(cursor);
            Gui.PushStyleVar(ImGuiStyleVar.ItemSpacing, NVector2.Zero);
            try { Gui.Dummy(NVector2.Zero); }
            finally { Gui.PopStyleVar(); }
            Gui.GetCurrentContext().LastItemData = item;
        }
    }
    private void DrawElementCommands(UIDocument source, UIElement element)
    {
        var ids = selected.ToArray();
        var editable = host.CanEditHierarchyOwner(source);
        var roots = source.CommonRoots(ids).Select(source.Find).OfType<UIElement>().ToArray();
        var canGroup = editable && AreUISiblings(source, ids);
        if (host.BeginMenu("Group into", canGroup))
        {
            foreach (var factory in host.Factories.Where(factory => factory.IsPanelFactory))
                if (host.DrawMenuItem(factory.Name + "##" + factory.ElementId))
                    QueueUIGrouping(source, ids, factory);
            Gui.EndMenu();
        }
        if (element is Panel)
        {
            if (host.DrawMenuItem("Ungroup", enabled: editable && source.CanUngroup(element.Id)))
                QueueUICommand(source, [element.Id], true, (asset, selection) => SelectUIResult(asset.Ungroup(selection[0])));
            if (host.BeginMenu("Change layout type", editable))
            {
                foreach (var factory in host.Factories.Where(factory => factory.IsPanelFactory))
                    if (host.DrawMenuItem(factory.Name + "##" + factory.ElementId))
                        QueueUICommand(source, [element.Id], true, (asset, selection) =>
                        {
                            if (!factory.IsPanelFactory) throw new InvalidOperationException("The UI panel factory is no longer available.");
                            SelectUIResult([asset.ReplacePanel(selection[0], factory)]);
                        }, factory);
                Gui.EndMenu();
            }
        }
        using (ImGuiScopes.Disabled(!editable)) DrawChildLayoutCommands(source, element);
        DrawSelectedLibraryCommands(source, ids, editable);
        if (host.DrawMenuItem("Create page from selection", enabled: editable && roots.Length == 1))
            QueueUICommand(source, ids, true, (asset, selection) => host.PublishAsset(asset.PrepareExport(selection, false), asset));
        if (host.DrawMenuItem("Create library from selection", enabled: editable && roots.Length > 0))
            QueueUICommand(source, ids, true, (asset, selection) => host.PublishAsset(asset.PrepareExport(selection, true), asset));
        if (host.DrawMenuItem("Duplicate", "Ctrl+D", enabled: editable && roots.Length > 0 && roots.All(root => root.VisualParent is Panel))) ExecuteUICommand("duplicate");
        if (host.DrawMenuItem("Copy", "Ctrl+C", enabled: ids.Length > 0))
            QueueUICommand(source, ids, false, (asset, selection) => Gui.SetClipboardText(asset.CopyHierarchy(selection)));
        if (host.DrawMenuItem("Cut", "Ctrl+X", enabled: editable && ids.Length > 0))
            QueueUICommand(source, ids, true, (asset, selection) => { var text = asset.CopyHierarchy(selection); Gui.SetClipboardText(text); if (Gui.GetClipboardTextS() != text) throw new InvalidOperationException("The UI hierarchy could not be written to the clipboard."); asset.Delete(selection); SelectUIResult([]); });
        var clipboard = Gui.GetClipboardTextS() ?? ""; var parent = ids.Length == 1 ? ids[0] : (Guid?)null;
        if (host.DrawMenuItem("Paste", "Ctrl+V", enabled: editable && source.CanPasteHierarchy(clipboard, parent)))
            QueueUICommand(source, ids, true, (asset, _) => SelectUIResult(asset.PasteHierarchy(clipboard, parent, host.ResolveLibrary)));
        if (host.DrawMenuItem("Delete", "Delete", enabled: editable && ids.Length > 0)) ExecuteUICommand("delete");
        if (host.DrawMenuItem("Rename", "F2", enabled: editable && ids.Length == 1 && source.CanRenameElement(ids[0]))) ExecuteUICommand("rename");
        host.RecordDiagnosticControl("ui/menu/rename");
    }
    private void DrawCanvasContext(UIDocument source, NVector2 origin, NVector2 size)
    {
        if (!Gui.BeginPopupContextItem("UI canvas context")) return;
        try
        {
            if (Gui.IsWindowAppearing()) canvasContextHits = viewport?.ElementsAtPosition(Gui.GetIO().MousePos - origin, size).ToArray() ?? [];
            if (canvasContextHits.Length > 0 && host.BeginMenu("Set current selection"))
            {
                foreach (var id in canvasContextHits)
                    if (source.Find(id) is { } hit && host.DrawMenuItem((hit.Name ?? hit.GetType().Name) + "##" + id, selected: PrimarySelection == id))
                        Select(id, false);
                Gui.EndMenu();
            }
            if (PrimarySelection is { } selectedId && source.Find(selectedId) is { } element) DrawElementCommands(source, element);
            DrawCanvasViewCommands(source);
        }
        finally { Gui.EndPopup(); }
    }
}
