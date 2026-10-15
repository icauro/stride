using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class HierarchyPanel
{
    private readonly IHierarchyPanelHost host;
    internal HierarchyPanelState State { get; } = new();
    internal HierarchyPanel(IHierarchyPanelHost host) => this.host = host;
    internal void Render(EditorIcons icons)
    {
        State.DropRows.Clear();
        if (!State.Visible) { ClearKeyboardRows(); return; }
        using var window = ImGuiScopes.Window(EditorPanels.Hierarchy.DisplayWindowName, ref State.Visible);
        if (window.DrawContents)
        {
            using var font = ImGuiScopes.Font(12);
            using var padding = ImGuiScopes.Style(ImGuiStyleVar.FramePadding, new NVector2(2 * Gui.GetStyle().FontScaleDpi));
            using var spacing = ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, new NVector2(4, 2) * Gui.GetStyle().FontScaleDpi);
            using var indent = ImGuiScopes.Style(ImGuiStyleVar.IndentSpacing, 12 * Gui.GetStyle().FontScaleDpi);
            if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) host.ActivateSceneContext();
            if (Gui.IsWindowDocked()) host.RecordDocked();
            Gui.BeginDisabled(host.Document?.CanCreateEntity(null) != true && !host.CanCreateEntityInSelection);
            if (host.HierarchyIconButton("create-entity", "Create a new entity")) Gui.OpenPopup("Create entity");
            if (host.Controls.Enabled) host.Controls.Record("hierarchy/create");
            if (Gui.BeginPopup("Create entity"))
            {
                if (host.DrawMenuItem("Empty entity at root", enabled: host.Document?.CanCreateEntity(null) == true)) host.Execute(() => host.CreateEmptyEntity(null));
                if (host.Controls.Enabled) host.Controls.Record("hierarchy/create/root");
                if (host.DrawMenuItem("Empty entity in selection", "", false, host.CanCreateEntityInSelection))
                    host.Execute(host.CreateEmptyEntityInSelection);
                if (host.Controls.Enabled) host.Controls.Record("hierarchy/create/child");
                host.DrawEntityFactories();
                host.DrawHierarchyFolderCreationMenu();
                Gui.EndPopup();
            }
            Gui.EndDisabled(); Gui.SameLine();
            var toolbarMinimum = Gui.GetItemRectMin();
            var buttonWidth = Gui.GetTextLineHeight() + 4 * Gui.GetStyle().FontScaleDpi;
            Gui.SetNextItemWidth(Math.Max(buttonWidth, Gui.GetContentRegionAvail().X - 2 * (buttonWidth + Gui.GetStyle().ItemSpacing.X)));
            Gui.InputTextWithHint("##HierarchyFilter", "Filter", ref State.hierarchyFilter, 256);
            if (host.Controls.Enabled) host.Controls.Record("hierarchy/filter");
            Gui.SameLine();
            if (host.HierarchyIconButton("hierarchy-expand", "Expand all entities")) { State.hierarchyExpandAll = true; State.hierarchyExpansionRevision++; }
            Gui.SameLine();
            if (host.HierarchyIconButton("hierarchy-collapse", "Collapse all entities")) { State.hierarchyExpandAll = false; State.hierarchyExpansionRevision++; }
            host.RegisterVisualCaptureRegion("hierarchy.toolbar", "toolbar", "Hierarchy toolbar", toolbarMinimum, Gui.GetItemRectMax(), "scene.hierarchy");
            if (!host.PrimaryHierarchyRootSelected && Gui.IsWindowFocused() && !Gui.IsAnyItemActive() && Gui.IsKeyPressed(ImGuiKey.F2) && host.SelectedFolder is { } folder)
                host.RequestHierarchyFolderRename(folder.Owner, folder.Path);
            else if (!host.PrimaryHierarchyRootSelected && Gui.IsWindowFocused() && !Gui.IsAnyItemActive() && Gui.IsKeyPressed(ImGuiKey.F2)
                && host.PrimarySelection is { } rename && rename.Document.CanRenameEntity(rename.Entity))
                host.BeginEntityRename(rename.Document, rename.Entity);
            if (Gui.IsWindowFocused() && !Gui.IsAnyItemActive() && Gui.IsKeyPressed(ImGuiKey.Delete))
                host.RequestSelectedEntitiesDeletion();
            if (Gui.IsWindowFocused() && !Gui.IsAnyItemActive() && Gui.IsKeyPressed(ImGuiKey.F) && host.PrimarySelection is { } focus)
                host.FocusEntity(focus.Document, focus.Entity);
            host.HandleHierarchyClipboardShortcuts();
            Gui.Separator();
            State.hierarchyTree.Begin();
            BeginKeyboardRows();
            host.BeginHierarchyContentRows();
            State.hierarchyDisplayed.Clear(); State.pendingHierarchySelection = null;
            if (State.hierarchySelectionDocument != host.Document)
            { State.hierarchySelectionDocument = host.Document; State.hierarchySelectionAnchor = null; }
            if (host.Document != null)
            {
                UpdateHierarchyVisibility();
                if (host.PresentedDocument is SceneDocument presented) host.DrawCompositeSceneHierarchy(presented);
                else if (host.Document is PrefabDocument prefab)
                {
                    Gui.PushID(prefab.Asset.Id.ToString());
                    Gui.SetNextItemOpen(true, ImGuiCond.Once);
                    host.RegisterPrefabRootRow(prefab);
                    var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
                    if (host.IsPrefabRootSelected(prefab)) flags |= ImGuiTreeNodeFlags.Selected;
                    var open = DrawRow("Prefab root", "entity-scene", flags, false, true);
                    if (Gui.IsItemClicked() && !Gui.IsItemToggledOpen())
                        host.QueueHierarchyKeyboardSelection(HierarchyContentItem.Root(prefab), Gui.GetIO().KeyCtrl, Gui.GetIO().KeyShift);
                    if (host.Controls.Enabled) host.Controls.Record("hierarchy/drop/root");
                    host.DrawEntityDragDrop(null);
                    host.DrawHierarchyRootContext(prefab);
                    if (open) { host.DrawHierarchyChildren(null); Gui.TreePop(); }
                    Gui.PopID();
                }
                else
                {
                    DrawRow(Path.GetFileNameWithoutExtension(host.Document.Path), "entity-scene",
                        ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth, false);
                    if (host.Controls.Enabled) host.Controls.Record("hierarchy/drop/root");
                    host.DrawEntityDragDrop(null);
                    host.DrawHierarchyRootContext(host.Document);
                    host.DrawHierarchyChildren(null);
                }
                ApplyHierarchySelection();
                CompleteKeyboardRows();
                host.CompleteHierarchyContentSelection();
                host.DrawHierarchyEmptyAreaDrop();
            }
            host.ApplyHierarchyDrop();
            host.ApplyHierarchyFolderDrop();
            host.DrawHierarchyFolderRename();
            host.ApplyHierarchyPrefabNavigation();
        }
        else ClearKeyboardRows();
    }

    private void UpdateHierarchyVisibility()
    {
        State.hierarchyVisible.Clear();
        if (State.hierarchyTokenSource != State.hierarchyFilter)
        {
            State.hierarchyTokenSource = State.hierarchyFilter;
            State.hierarchyFilterTokens = State.hierarchyFilter.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        }
        bool Visit(Entity entity)
        {
            var visible = host.RenameEntity == entity;
            if (!visible)
            {
                visible = true;
                foreach (var token in State.hierarchyFilterTokens)
                    if (!entity.Name.Contains(token, StringComparison.OrdinalIgnoreCase)) { visible = false; break; }
            }
            foreach (var child in entity.Transform.Children) visible |= Visit(child.Entity);
            if (!visible)
                foreach (var folder in host.Document!.GetHierarchyFolders(entity.Id))
                {
                    var name = folder[(folder.LastIndexOf('/') + 1)..];
                    if (State.hierarchyFilterTokens.All(token => name.Contains(token, StringComparison.OrdinalIgnoreCase))) { visible = true; break; }
                }
            if (visible) State.hierarchyVisible.Add(entity.Id);
            return visible;
        }
        foreach (var root in host.Document!.Asset.Hierarchy.RootParts) Visit(root);
    }

    private void ApplyHierarchySelection()
    {
        if (State.pendingHierarchyGroupClick is { } click)
        {
            State.hierarchyGroupClickDragged |= Gui.IsMouseDragging(ImGuiMouseButton.Left);
            if (Gui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                if (!State.hierarchyGroupClickDragged && click.Document.Asset.Hierarchy.Parts.ContainsKey(click.Entity.Id)
                    && host.IsPresentedHierarchy(click.Document))
                {
                    host.QueueHierarchyEntityClick(click.Document, click.Entity.Id, false, false);
                }
                State.pendingHierarchyGroupClick = null;
            }
            else if (click.Document != host.Document) State.pendingHierarchyGroupClick = null;
        }
        if (State.pendingHierarchySelection is not { } request) return;
        host.PrepareHierarchySelection();
        if (!request.Toggle) host.ClearOtherHierarchySelection(host.Document!);
        var start = State.hierarchySelectionAnchor is { } anchor ? State.hierarchyDisplayed.IndexOf(anchor) : -1;
        var end = State.hierarchyDisplayed.IndexOf(request.Entity.Id);
        if (request.Range && start >= 0 && end >= 0)
            host.Document!.SelectRange(State.hierarchyDisplayed.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1), request.Entity.Id, request.Toggle);
        else
        {
            host.Document!.SelectEntity(request.Entity, request.Toggle);
            State.hierarchySelectionAnchor = request.Entity.Id;
        }
        host.SetPrimarySelection(host.Document!, request.Entity.Id);
        State.pendingHierarchySelection = null;
    }
}
