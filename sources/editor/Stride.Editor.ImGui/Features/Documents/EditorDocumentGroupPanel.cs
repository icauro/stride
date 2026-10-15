using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class EditorDocumentGroupPanel(IEditorDocumentGroupHost host)
{
    internal EditorDocumentGroups Model { get; } = new();
    internal bool HasPendingNativeRestoration
        => adapters.Values.Any(adapter => adapter.RequestedSelection != null || adapter.RequestedFocus != null);
    internal object? InspectNativeTabs(Guid groupId) => adapters.GetValueOrDefault(groupId)?.Inspection;
    private readonly EditorDocumentGroupDocking docking = new();
    private readonly Dictionary<Guid, EditorDocumentTabAdapter> adapters = [];
    private readonly Dictionary<Guid, Guid> selectedTabs = [];
    private readonly List<(EditorDocumentEntry Entry, int Order)> tabOrder = [];
    private readonly List<Action> actions = [];
    private readonly HashSet<Guid> presentGroups = [];

    internal void Draw()
    {
        Model.Synchronize(host.OpenDocuments);
        var topologyChanged = docking.UpdateTopology(Model);
        if (host.RequestedSelection is { } requested && Model.Find(requested) != null) Model.Select(requested);
        actions.Clear(); presentGroups.Clear();
        foreach (var group in Model.Groups)
        {
            // A transfer is an explicit activation intent. Keep it until the
            // destination's actual native tab bar acknowledges it, rather than
            // allowing its previously selected tab to replace the moved owner.
            if (adapters.TryGetValue(group.Id, out var pendingAdapter) && (pendingAdapter.RequestedSelection ?? pendingAdapter.RequestedFocus) is { } pendingId)
            {
                var pending = group.Items.FirstOrDefault(entry => entry.Id == pendingId);
                if (pending == null || host.RequestedSelection is { } newer && !ReferenceEquals(newer, pending)) pendingAdapter.CancelSelectionRequest();
                else Model.Select(pending);
            }
            presentGroups.Add(group.Id);
            if (group.Items.Count == 0 && group.Floating)
            { host.SetVisible(group, null, false); continue; }
            docking.BeforeBegin(group);
            if ((host.RequestedSelection is { } focusedEntry && ReferenceEquals(group.Active, focusedEntry)
                || pendingAdapter?.RequestedFocus is { } focusId && group.Active?.Id == focusId)
                && host.NativeSelectionsRestored && !Gui.IsAnyItemActive()
                && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) Gui.SetNextWindowFocus();
            using (var window = ImGuiScopes.Window(group.WindowName, ImGuiWindowFlags.None))
            {
                ImGuiScopes.TraceLayoutPhase($"group-begin count={group.Items.Count} active={group.Active?.Id} draw={window.DrawContents}");
                docking.Observe(group);
                host.SetVisible(group, group.Active, window.DrawContents);
                ImGuiScopes.TraceLayoutPhase($"group-after-observe count={group.Items.Count}");
                // Keep the empty dock surface while project loading publishes
                // documents, but do not create a native tab bar with no items.
                // EndTabBar otherwise moves its cursor beyond registered content.
                if (!window.DrawContents || group.Items.Count == 0) continue;
                if (!adapters.TryGetValue(group.Id, out var adapter)) adapters.Add(group.Id, adapter = new());
                var requestSelection = group.Active != null && selectedTabs.GetValueOrDefault(group.Id) != group.Active.Id;
                tabOrder.Clear();
                var acceptNativeOrder = false;
                ImGuiScopes.TraceLayoutPhase("group-before-BeginTabBar");
                using (var tabs = ImGuiScopes.TabBar("Documents", ImGuiTabBarFlags.Reorderable))
                {
                    ImGuiScopes.TraceLayoutPhase($"group-after-BeginTabBar draw={tabs.DrawContents}");
                    if (!tabs.DrawContents) continue;
                    foreach (var entry in group.Items)
                    {
                        var open = true;
                        var title = entry.Name + " · " + entry.Kind;
                        var selected = adapter.BeginItem(entry.Id, title, entry.Document.IsDirty,
                            requestSelection && ReferenceEquals(group.Active, entry), ref open, out var order, host.NativeTabText, host.DiagnosticControlsEnabled);
                        var clickedTab = Gui.IsItemClicked(ImGuiMouseButton.Left);
                        if (clickedTab && (adapter.RequestedSelection ?? adapter.RequestedFocus) is { } pendingSelection && pendingSelection != entry.Id)
                            adapter.CancelSelectionRequest();
                        host.RecordDiagnosticTab(entry);
                        ImGuiScopes.TraceLayoutPhase($"group-after-BeginTabItem selected={selected} entry={entry.Id}");
                        try
                        {
                            if (order >= 0) tabOrder.Add((entry, order));
                            if (!host.NativeTabText)
                            {
                                var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
                                var draw = Gui.GetWindowDrawList();
                                draw.PushClipRect(minimum, maximum - new System.Numerics.Vector2(Gui.GetFrameHeight() * 1.5f, 0), true);
                                try { draw.AddText(minimum + Gui.GetStyle().FramePadding, Gui.GetColorU32(ImGuiCol.Text), title); }
                                finally { draw.PopClipRect(); }
                            }
                            if (Gui.IsItemHovered())
                            {
                                Gui.BeginTooltip(); try { Gui.TextUnformatted(entry.Path); } finally { Gui.EndTooltip(); }
                            }
                            // Unselected tab items do not push their native ID.
                            // Keep each popup tied to its document in either state.
                            if (Gui.BeginPopupContextItem("Document actions###document-actions/" + entry.Id))
                            {
                                try
                                {
                                    if (host.MenuItem("Move tab left")) adapter.QueueReorder(entry.Id, -1);
                                    if (host.MenuItem("Move tab right")) adapter.QueueReorder(entry.Id, 1);
                                    if (host.MenuItem("Close")) actions.Add(() => host.RequestClose(entry));
                                    host.RecordDiagnosticClose(entry, "close");
                                    if (host.MenuItem("Close All But This", host.CanCloseCohort && host.OpenDocuments.Count > 1))
                                        actions.Add(() => host.RequestCloseCohort(entry));
                                    host.RecordDiagnosticClose(entry, "others");
                                    if (host.MenuItem("Close All", host.CanCloseCohort && host.OpenDocuments.Count > 0))
                                        actions.Add(() => host.RequestCloseCohort(null));
                                    host.RecordDiagnosticClose(entry, "all");
                                    DrawMove(entry, EditorDocumentGroupCommand.Float, "Float");
                                    DrawMove(entry, EditorDocumentGroupCommand.Dock, "Dock as Tabbed Document");
                                    DrawMove(entry, EditorDocumentGroupCommand.Horizontal, "New Horizontal Tab Group");
                                    DrawMove(entry, EditorDocumentGroupCommand.Vertical, "New Vertical Tab Group");
                                    DrawMove(entry, EditorDocumentGroupCommand.Next, "Move To Next Tab Group");
                                    DrawMove(entry, EditorDocumentGroupCommand.Previous, "Move To Previous Tab Group");
                                }
                                finally { Gui.EndPopup(); }
                            }
                            if (!open) actions.Add(() => host.RequestClose(entry));
                            if (!selected) continue;
                            // SetSelected may take effect on the next native tab
                            // layout. Do not mistake the previously visible tab
                            // for a new user selection and overwrite the pending
                            // canonical activation before it is presented.
                            if (requestSelection && !ReferenceEquals(group.Active, entry) && !clickedTab) continue;
                            var focused = IsCurrentGroupFocused();
                            if (host.NativeSelectionsRestored && ReferenceEquals(group.Active, entry)) adapter.CompleteFocus(entry.Id, focused);
                            if (!ReferenceEquals(group.Active, entry) || focused && !ReferenceEquals(host.ActiveDocument, entry))
                                actions.Add(() => ActivatePresentation(entry));
                            if (ReferenceEquals(group.Active, entry) && host.CanPresent(group, entry))
                            {
                                ImGuiScopes.TraceLayoutPhase("group-before-DrawContents");
                                host.DrawContents(group, entry, focused);
                                ImGuiScopes.TraceLayoutPhase("group-after-DrawContents");
                            }
                        }
                        finally
                        {
                            if (selected)
                            {
                                ImGuiScopes.TraceLayoutPhase("group-before-EndTabItem");
                                Gui.EndTabItem();
                                ImGuiScopes.TraceLayoutPhase("group-after-EndTabItem");
                            }
                        }
                    }
                    // BeginTabItem may return the formerly visible tab while a
                    // newly transferred SetSelected tab is awaiting native layout.
                    // Acknowledge selection only after the entire bar has settled.
                    if (group.Active is { } active && adapter.SelectionSettled(active.Id)) selectedTabs[group.Id] = active.Id;
                    acceptNativeOrder = adapter.CompleteOrder(group.Items);
                }
                // Group-only reorder does not reorder, reopen or replace authored documents.
                var reordered = false;
                for (var index = 0; index < tabOrder.Count; index++)
                    reordered |= index >= group.Items.Count || !ReferenceEquals(tabOrder[index].Entry, group.Items[index]) || tabOrder[index].Order != index;
                if (acceptNativeOrder && tabOrder.Count == group.Items.Count && reordered)
                {
                    var order = tabOrder.ToArray();
                    actions.Add(() => Model.ApplyOrder(group, order));
                }
            }
        }
        foreach (var action in actions) host.Execute(action);
        var nativeMerge = false;
        host.Execute(() => nativeMerge = docking.Reconcile(Model, group => host.SetVisible(group, group.Active, false)));
        topologyChanged |= docking.UpdateTopology(Model);
        if (actions.Count != 0 || nativeMerge || topologyChanged)
        {
            Model.Synchronize(host.OpenDocuments);
            var order = Model.Groups.SelectMany(group => group.Items).ToArray();
            if (!order.SequenceEqual(host.OpenDocuments)) host.Execute(() => host.ApplyPresentationOrder(order));
        }
        // Deferred commands can create groups after this frame's draw inventory
        // was collected. Retain their native selection and placement intents
        // until their first draw; remove only groups absent from the final model.
        presentGroups.Clear();
        foreach (var group in Model.Groups) presentGroups.Add(group.Id);
        foreach (var id in adapters.Keys.Where(id => !presentGroups.Contains(id)).ToArray())
        { adapters.Remove(id); selectedTabs.Remove(id); docking.Forget(id); }
    }

    private void DrawMove(EditorDocumentEntry entry, EditorDocumentGroupCommand command, string caption)
    {
        if (!Model.CanMove(entry, command)) return; // Native group commands hide unavailable operations.
        var move = Model.Plan(entry, command);
        var enabled = docking.CanApply(move) && host.CanMove(move);
        var activated = host.MenuItem(caption, enabled);
        host.RecordDiagnosticCommand(entry, command);
        if (!activated) return;
        actions.Add(() =>
        {
            if (!host.OpenDocuments.Contains(entry) || !Model.CanMove(entry, command)
                || !ReferenceEquals(entry.Document, move.Document) || !ReferenceEquals(entry.State, move.State)
                || !host.CanMove(move) || !docking.CanApply(move)) return;
            var source = Model.Find(move.Source);
            if (source == null || !ReferenceEquals(Model.Find(entry), source)) return;
            var destination = Model.Find(move.Destination)?.WindowName ?? entry.Name + "###document-group/" + move.Destination;
            // Native topology must not change while the old owner has a draft
            // that rejects focus validation. Activation owns that preflight.
            ActivatePresentation(entry);
            if (!ReferenceEquals(entry.Document, move.Document) || !ReferenceEquals(entry.State, move.State)
                || !host.CanMove(move) || !docking.CanApply(move) || !ReferenceEquals(Model.Find(entry), source)) return;
            docking.Apply(move, source.WindowName, destination);
            Model.Apply(move);
            var target = Model.Find(entry);
            if (target != null)
            {
                if (!adapters.TryGetValue(target.Id, out var targetAdapter)) adapters.Add(target.Id, targetAdapter = new());
                targetAdapter.RequestOrder(target.Items);
                targetAdapter.RequestSelection(entry.Id);
                selectedTabs.Remove(target.Id);
            }
        });
    }

    private void ActivatePresentation(EditorDocumentEntry entry)
    {
        try { host.Activate(entry); Model.Select(entry); }
        catch
        {
            host.ActivationRejected(entry);
            // ImGui has already changed its native selected tab/focus. Force
            // each group's still-authoritative selection on the next frame,
            // and restore focus to the actual command owner immediately.
            selectedTabs.Clear();
            if (host.ActiveDocument is { } current && Model.Find(current) is { } group)
            {
                Model.Select(current);
                Gui.SetWindowFocus(group.WindowName);
            }
            throw;
        }
    }

    private static bool IsCurrentGroupFocused()
    {
        var window = ImGuiP.GetCurrentWindowRead();
        var focused = Gui.GetCurrentContext().NavWindow;
        // Docking focus predicates may treat a sibling's dockspace host as a
        // shared root. A document owns focus only in its actual window subtree.
        for (var depth = 0; !window.IsNull && !focused.IsNull && depth < 128; depth++, focused = focused.ParentWindow)
            if (focused.ID == window.ID) return true;
        return false;
    }

    internal EditorDocumentGroupsLayout CaptureLayout(string workspace, string? activePath = null) => Model.Capture(workspace, activePath);
    internal void ResetLayout()
    {
        var entries = host.OpenDocuments.ToArray(); var active = host.ActiveDocument;
        var owners = entries.Select(entry => (entry.Document, entry.State)).ToArray();
        // Release visibility/input only; the document entry and engine view
        // remain owned by their existing host throughout the layout reset.
        foreach (var group in Model.Groups) host.SetVisible(group, group.Active, false);
        if (!host.OpenDocuments.SequenceEqual(entries) || !ReferenceEquals(host.ActiveDocument, active)
            || entries.Where((entry, index) => !ReferenceEquals(entry.Document, owners[index].Document)
                || !ReferenceEquals(entry.State, owners[index].State)).Any())
            throw new InvalidOperationException("The document owners changed while hiding their layout views.");
        Model.Clear();
        Model.Synchronize(entries);
        if (active != null && Model.Find(active) != null) Model.Select(active);
        docking.Clear(); adapters.Clear(); selectedTabs.Clear(); actions.Clear(); presentGroups.Clear();
    }
    internal void SetDocumentArea(uint dockId, bool initializePrimary = false) => docking.SetDocumentArea(dockId, initializePrimary);
    internal void RestoreLayout(EditorDocumentGroupsLayout layout, string workspace)
    {
        // The host must prepare actual concurrently visible engines before restoring extra groups.
        var candidate = new EditorDocumentGroups();
        candidate.Restore(layout, workspace, host.OpenDocuments);
        foreach (var group in candidate.Groups)
            if (group.Active is { } entry && !host.CanPresent(group, entry))
                throw new InvalidOperationException("The restored document view is not ready for independent presentation.");
        // Clear registrations owned by the previous group IDs only after the
        // complete candidate passed validation. This releases input/visibility,
        // retaining all canonical documents and engine views for restoration.
        foreach (var group in Model.Groups) host.SetVisible(group, null, false);
        Model.Restore(layout, workspace, host.OpenDocuments);
        docking.Clear(); adapters.Clear(); selectedTabs.Clear();
        foreach (var group in Model.Groups)
        {
            if (group.Active is not { } entry) continue;
            var adapter = new EditorDocumentTabAdapter();
            adapter.RequestOrder(group.Items);
            adapter.RequestSelection(entry.Id, ReferenceEquals(host.ActiveDocument, entry));
            adapters.Add(group.Id, adapter);
        }
    }
    internal void Clear() { Model.Clear(); docking.Clear(); adapters.Clear(); selectedTabs.Clear(); actions.Clear(); presentGroups.Clear(); }
}
