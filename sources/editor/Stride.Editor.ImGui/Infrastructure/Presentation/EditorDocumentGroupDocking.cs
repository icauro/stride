using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Native docking intents; only managed IDs survive the current native UI call.</summary>
internal sealed unsafe class EditorDocumentGroupDocking
{
    private readonly Dictionary<Guid, uint> docks = [];
    private readonly HashSet<Guid> floatOnBegin = [];
    private readonly Dictionary<Guid, (System.Numerics.Vector2 Position, System.Numerics.Vector2 Size)> floatingPlacement = [];
    private readonly Dictionary<Guid, bool> focused = [];
    private readonly Dictionary<Guid, (System.Numerics.Vector2 Position, System.Numerics.Vector2 Size, uint Viewport)> geometry = [];
    private readonly Dictionary<uint, Guid> paneGroups = [];
    private readonly Dictionary<Guid, EditorDocumentGroupNeighbors> neighbors = [];
    private readonly List<uint> roots = [], familyPanes = [];
    private readonly List<Guid> topologyOrder = [];
    private uint documentArea;
    private bool initialDockPending;

    internal void SetDocumentArea(uint id, bool initializePrimary)
    {
        documentArea = id;
        initialDockPending = initializePrimary;
    }
    internal void Observe(EditorDocumentGroup group)
    {
        var dock = Gui.GetWindowDockID();
        docks[group.Id] = dock;
        var node = dock == 0 ? null : ImGuiP.DockBuilderGetNode(dock).Handle;
        // Authored tabs are drawn inside the document group. Hide only the
        // redundant single-window native dock tab; sharing the pane with a
        // preview or another tool must still expose native window switching.
        if (node != null) node->LocalFlags |= ImGuiDockNodeFlags.AutoHideTabBar;
        group.Floating = node == null || !IsMainRoot(Root(node));
        focused[group.Id] = Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        geometry[group.Id] = (Gui.GetWindowPos(), Gui.GetWindowSize(), Gui.GetWindowViewport().ID);
        if (group.Id == EditorDocumentGroups.PrimaryId && dock != 0 && !group.Floating) documentArea = dock;
    }
    internal bool Reconcile(EditorDocumentGroups model, Action<EditorDocumentGroup> beforeMerge)
    {
        var changed = false;
        // Actual native pane tabbing merges document cohorts, rather than keeping two nested bars in one pane.
        for (var index = model.Groups.Count - 1; index >= 0; index--)
        {
            var source = model.Groups[index]; var dock = docks.GetValueOrDefault(source.Id);
            if (source.Id == EditorDocumentGroups.PrimaryId || !ValidDock(dock)) continue;
            var primary = model.Primary;
            if (docks.GetValueOrDefault(primary.Id) == dock)
            {
                beforeMerge(source); model.Merge(source, primary, focused.GetValueOrDefault(source.Id));
                Forget(source.Id); changed = true; continue;
            }
            for (var targetIndex = 0; targetIndex < index; targetIndex++)
            {
                var destination = model.Groups[targetIndex];
                if (docks.GetValueOrDefault(destination.Id) != dock) continue;
                beforeMerge(source);
                model.Merge(source, destination, focused.GetValueOrDefault(source.Id));
                Forget(source.Id); changed = true; break;
            }
        }
        return changed;
    }

    private static ImGuiDockNode* Child(ImGuiDockNode* node, int index) => (&node->ChildNodes_0)[index];
    private static ImGuiDockNode* Root(ImGuiDockNode* node)
    {
        var depth = 0;
        while (node != null && node->ParentNode != null)
        { if (++depth > 128) throw new InvalidOperationException("Native docking ancestry is invalid."); node = node->ParentNode; }
        return node;
    }
    private static bool IsMainRoot(ImGuiDockNode* node) => node != null
        && (node->MergedFlags & (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space) != 0;

    internal bool UpdateTopology(EditorDocumentGroups model)
    {
        paneGroups.Clear(); roots.Clear(); neighbors.Clear(); topologyOrder.Clear();
        foreach (var group in model.Groups)
        {
            var window = ImGuiP.FindWindowByName(group.WindowName);
            if (!window.IsNull && window.WasActive) docks[group.Id] = window.DockId;
            var dock = docks.GetValueOrDefault(group.Id);
            var node = dock == 0 ? null : ImGuiP.DockBuilderGetNode(dock).Handle;
            var root = Root(node);
            group.Floating = !IsMainRoot(root);
            if (node == null) continue;
            if (!paneGroups.ContainsKey(dock) || group.Id == EditorDocumentGroups.PrimaryId) paneGroups[dock] = group.Id;
            if (!roots.Contains(root->ID))
            {
                if (IsMainRoot(root)) roots.Insert(0, root->ID);
                else roots.Add(root->ID);
            }
        }
        foreach (var root in roots) Visit(ImGuiP.DockBuilderGetNode(root).Handle, 0);
        foreach (var group in model.Groups)
        {
            if (!topologyOrder.Contains(group.Id)) topologyOrder.Add(group.Id);
            var dock = docks.GetValueOrDefault(group.Id);
            var node = dock == 0 ? null : ImGuiP.DockBuilderGetNode(dock).Handle;
            if (node == null || node->ParentNode == null) continue;
            var family = node->ParentNode;
            while (family->ParentNode != null && family->ParentNode->SplitAxis == family->SplitAxis)
                family = family->ParentNode;
            familyPanes.Clear(); VisitFamily(family, (int)family->SplitAxis, 0);
            var index = familyPanes.IndexOf(dock);
            if (index < 0) continue;
            var previous = index > 0 ? paneGroups.GetValueOrDefault(familyPanes[index - 1]) : Guid.Empty;
            var next = index + 1 < familyPanes.Count ? paneGroups.GetValueOrDefault(familyPanes[index + 1]) : Guid.Empty;
            neighbors[group.Id] = new(previous, next);
        }
        return model.ApplyNativeTopology(topologyOrder, neighbors);
    }

    private void Visit(ImGuiDockNode* node, int depth)
    {
        if (node == null) return;
        if (depth > 128) throw new InvalidOperationException("Native docking topology is invalid.");
        if (paneGroups.TryGetValue(node->ID, out var group)) topologyOrder.Add(group);
        Visit(Child(node, 0), depth + 1); Visit(Child(node, 1), depth + 1);
    }
    private void VisitFamily(ImGuiDockNode* node, int axis, int depth)
    {
        if (node == null) return;
        if (depth > 128) throw new InvalidOperationException("Native docking family is invalid.");
        if (Child(node, 0) != null && (int)node->SplitAxis == axis)
        { VisitFamily(Child(node, 0), axis, depth + 1); VisitFamily(Child(node, 1), axis, depth + 1); }
        else familyPanes.Add(node->ID); // Other tools/mixed-axis subgroups are real immediate-sibling boundaries.
    }
    internal void BeforeBegin(EditorDocumentGroup group)
    {
        if (group.Floating || floatOnBegin.Contains(group.Id))
        {
            // Float is a separate document window, even when its initial
            // rectangle overlaps the main editor's client area.
            var windowClass = new ImGuiWindowClass { ViewportFlagsOverrideSet = ImGuiViewportFlags.NoAutoMerge };
            Gui.SetNextWindowClass(ref windowClass);
        }
        if (floatOnBegin.Remove(group.Id))
        {
            Gui.SetNextWindowDockID(0, ImGuiCond.Always);
            if (floatingPlacement.Remove(group.Id, out var bounds))
            {
                Gui.SetNextWindowPos(bounds.Position, ImGuiCond.Always);
                Gui.SetNextWindowSize(bounds.Size, ImGuiCond.Always);
            }
        }
        else if (group.Id == EditorDocumentGroups.PrimaryId && initialDockPending && ValidDock(documentArea))
        { Gui.SetNextWindowDockID(documentArea, ImGuiCond.Always); initialDockPending = false; }
    }
    private static bool ValidDock(uint id) => id != 0 && !ImGuiP.DockBuilderGetNode(id).IsNull;
    private static bool MainDock(uint id) => ValidDock(id) && IsMainRoot(Root(ImGuiP.DockBuilderGetNode(id).Handle));
    private uint DockTarget(EditorDocumentGroupMove move)
    {
        var id = docks.GetValueOrDefault(move.Destination);
        // A valid dock node may belong to a detached floating family. Dock as
        // Tabbed Document must return to the actual main document dockspace.
        return MainDock(id) ? id : move.Destination == EditorDocumentGroups.PrimaryId && MainDock(documentArea) ? documentArea : 0;
    }
    internal bool CanApply(EditorDocumentGroupMove move) => move.Command switch
    {
        EditorDocumentGroupCommand.Float => true,
        EditorDocumentGroupCommand.Next => neighbors.GetValueOrDefault(move.Source).Next == move.Destination,
        EditorDocumentGroupCommand.Previous => neighbors.GetValueOrDefault(move.Source).Previous == move.Destination,
        EditorDocumentGroupCommand.Dock => DockTarget(move) != 0,
        _ => ValidDock(docks.GetValueOrDefault(move.Source)) || geometry.TryGetValue(move.Source, out var bounds)
            && bounds.Viewport != 0 && bounds.Size.X > 0 && bounds.Size.Y > 0,
    };
    internal unsafe void Apply(EditorDocumentGroupMove move, string sourceWindow, string destinationWindow)
    {
        if (!CanApply(move)) throw new InvalidOperationException("The actual document docking target is unavailable.");
        if (move.Command == EditorDocumentGroupCommand.Float)
        {
            if (!geometry.TryGetValue(move.Source, out var bounds)
                || !float.IsFinite(bounds.Size.X) || !float.IsFinite(bounds.Size.Y)
                || bounds.Size.X <= 0 || bounds.Size.Y <= 0)
                throw new InvalidOperationException("The source document pane has no valid floating bounds.");
            floatingPlacement[move.Destination] = (bounds.Position, bounds.Size);
            floatOnBegin.Add(move.Destination);
            return;
        }
        if (move.Command is EditorDocumentGroupCommand.Next or EditorDocumentGroupCommand.Previous) return;
        if (move.Command == EditorDocumentGroupCommand.Dock)
        {
            ImGuiP.DockBuilderDockWindow(destinationWindow, DockTarget(move));
            return;
        }
        var node = docks.GetValueOrDefault(move.Source);
        if (!ValidDock(node))
        {
            // A standalone floating window is a real native pane, even before it has a dock node.
            var bounds = geometry[move.Source];
            node = ImGuiP.DockBuilderAddNode(0, ImGuiDockNodeFlags.None);
            ImGuiP.DockBuilderSetNodePos(node, bounds.Position);
            ImGuiP.DockBuilderSetNodeSize(node, bounds.Size);
            ImGuiP.DockBuilderDockWindow(sourceWindow, node);
        }
        uint next, previous;
        ImGuiP.DockBuilderSplitNode(node, move.Command == EditorDocumentGroupCommand.Horizontal ? ImGuiDir.Down : ImGuiDir.Right,
            0.5f, &next, &previous);
        ImGuiP.DockBuilderDockWindow(sourceWindow, previous);
        ImGuiP.DockBuilderDockWindow(destinationWindow, next);
        ImGuiP.DockBuilderFinish(node);
    }
    internal void Forget(Guid group) { docks.Remove(group); floatOnBegin.Remove(group); floatingPlacement.Remove(group); focused.Remove(group); geometry.Remove(group); }
    internal void Clear()
    {
        docks.Clear(); floatOnBegin.Clear(); floatingPlacement.Clear(); focused.Clear(); geometry.Clear(); paneGroups.Clear(); neighbors.Clear();
        roots.Clear(); familyPanes.Clear(); topologyOrder.Clear(); documentArea = 0; initialDockPending = false;
    }
}
