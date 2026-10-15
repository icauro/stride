using Hexa.NET.ImGui;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class CompositorListKeyboardState
{
    internal ItemId? Focus;
    internal bool ApplyFocus, ScrollFocus;
}

internal sealed partial class EditorGame
{
    private static CompositorListKeyboardState CompositorKeyboardState(CompositorEditorState state, string collection)
    {
        if (!state.ListKeyboard.TryGetValue(collection, out var input)) state.ListKeyboard.Add(collection, input = new());
        return input;
    }
    private void HandleCompositorListKeyboard(AssetMetadataDocument source, CompositorEditorState state, string collection,
        IReadOnlyList<CompositorListItem> rows)
    {
        if (!Gui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) || Gui.GetIO().WantTextInput || Gui.IsAnyItemActive() || rows.Count == 0) return;
        var io = Gui.GetIO(); if (io.KeyAlt || io.KeySuper) return;
        var input = CompositorKeyboardState(state, collection);
        EditorListKeyboardInput.Claim("##CompositorListKeys");
        ImGuiP.SetKeyOwner(ImGuiKey.A, Gui.GetID("##CompositorListKeys"));
        var index = -1;
        for (var row = 0; row < rows.Count; row++) if (rows[row].ItemId == input.Focus) { index = row; break; }
        if (index < 0)
        {
            for (var row = 0; row < rows.Count; row++) if (rows[row].ItemId == state.PropertySelection?.Item) { index = row; break; }
            if (index < 0) index = 0;
        }
        var target = index; var navigate = false;
        var page = Math.Max(1, (int)((Gui.GetWindowSize().Y - Gui.GetStyle().WindowPadding.Y * 2) / Math.Max(1, Gui.GetTextLineHeightWithSpacing())));
        if (Gui.IsKeyPressed(ImGuiKey.UpArrow)) { target--; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.DownArrow)) { target++; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.Home)) { target = 0; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.End)) { target = rows.Count - 1; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.PageUp)) { target -= page; navigate = true; }
        else if (Gui.IsKeyPressed(ImGuiKey.PageDown)) { target += page; navigate = true; }
        if (navigate)
        {
            var item = rows[Math.Clamp(target, 0, rows.Count - 1)];
            input.Focus = item.ItemId; input.ApplyFocus = input.ScrollFocus = true;
            if (io.KeyShift || !io.KeyCtrl) SelectCompositorListItemAfterPending(source, state, item, io.KeyCtrl && io.KeyShift, io.KeyShift);
        }
        else if (Gui.IsKeyPressed(ImGuiKey.Space, false) && !(io.KeyCtrl && io.KeyShift))
            SelectCompositorListItemAfterPending(source, state, rows[index], io.KeyCtrl, io.KeyShift);
        else if (!io.KeyCtrl && !io.KeyShift && Gui.IsKeyPressed(ImGuiKey.F2, false)
            && state.SelectedItems.Contains(rows[index].ItemId) && source.CanRenameCompositorListItem(rows[index]))
            BeginCompositorListNameEdit(source, state, rows[index]);
        else if (io.KeyCtrl && !io.KeyShift && (Gui.IsKeyPressed(ImGuiKey.Slash, false) || Gui.IsKeyPressed(ImGuiKey.A, false)))
            ExecuteCompositorCommand(source, () =>
            {
                if (!compositorEditors.TryGetValue(source, out var current) || !ReferenceEquals(current, state))
                    throw new InvalidOperationException("The compositor selection owner changed.");
                var actual = source.CompositorItems.Where(item => item.Collection == collection).ToArray();
                if (actual.Length == 0) return;
                RetainCompositorListScope(state, actual);
                var added = false;
                foreach (var item in actual) added |= state.SelectedItems.Add(item.ItemId);
                state.PropertySelection = new(null, actual.FirstOrDefault(item => item.ItemId == input.Focus)?.ItemId ?? actual[0].ItemId);
                NotifyNativeCompositorSelection(source, collection, added);
            }, requiresEdit: false);
        else if (io.KeyCtrl && !io.KeyShift && Gui.IsKeyPressed(ImGuiKey.Backslash, false))
            SelectCompositorListItemAfterPending(source, state, rows[index], false, false);
    }
    private void SelectCompositorListItemAfterPending(AssetMetadataDocument source, CompositorEditorState state,
        CompositorListItem item, bool control, bool shift)
        => ExecuteCompositorCommand(source, () =>
        {
            if (!compositorEditors.TryGetValue(source, out var current) || !ReferenceEquals(current, state))
                throw new InvalidOperationException("The compositor selection owner changed.");
            var rows = source.CompositorItems.Where(row => row.Collection == item.Collection).ToArray();
            var actual = rows.FirstOrDefault(row => row.ItemId == item.ItemId && ReferenceEquals(row.Value, item.Value) && ReferenceEquals(row.Node, item.Node))
                ?? throw new InvalidOperationException("The compositor selection target changed during validation.");
            var added = ApplyCompositorListSelection(state, actual, rows, control, shift);
            NotifyNativeCompositorSelection(source, item.Collection, added);
        }, requiresEdit: false);
    private static void RetainCompositorListScope(CompositorEditorState state, IReadOnlyList<CompositorListItem> rows)
    {
        state.SelectedBlocks.Clear();
        state.SelectedItems.IntersectWith(rows.Select(row => row.ItemId));
    }
    private bool SelectCompositorBlocksAfterPending(AssetMetadataDocument source, CompositorEditorState state,
        IReadOnlyList<CompositorBlock> targets, bool control, bool shift, bool marquee)
    {
        var accepted = false;
        ExecuteCompositorCommand(source, () =>
        {
            if (!compositorEditors.TryGetValue(source, out var current) || !ReferenceEquals(current, state))
                throw new InvalidOperationException("The compositor selection owner changed.");
            var blocks = source.CompositorBlocks.ToArray();
            var added = false;
            foreach (var target in targets)
                if (!blocks.Any(block => block.Id == target.Id && ReferenceEquals(block.Value, target.Value) && ReferenceEquals(block.Node, target.Node)))
                    throw new InvalidOperationException("The compositor selection target changed during validation.");
            if (targets.Count == 0)
            {
                // Native area selection only clears the graph collections. Other registered
                // scopes change on an actual graph Add, not on a background release.
                if (!control && !shift)
                {
                    var changed = state.SelectedBlocks.Count != 0 || state.SelectedLinks.Count != 0;
                    state.SelectedBlocks.Clear(); state.SelectedLinks.Clear(); state.SelectedLinkCertificates.Clear();
                    if (changed)
                    {
                        state.ClearGraphInspectorSelection();
                        source.ClearCompositorPresentationRoots();
                        NotifyNativeCompositorSelection(source, "blocks");
                    }
                }
                accepted = true;
                return;
            }
            if (!control && !shift && (marquee || targets.Any(target => !state.SelectedBlocks.Contains(target.Id))))
            {
                state.SelectedBlocks.Clear(); state.SelectedLinks.Clear(); state.SelectedLinkCertificates.Clear(); state.SelectedItems.Clear(); state.PropertySelection = null;
            }
            foreach (var target in targets)
            {
                if (control && state.SelectedBlocks.Contains(target.Id)) state.SelectedBlocks.Remove(target.Id);
                else
                {
                    // Native SelectionService clears other registered list scopes on Add,
                    // including Ctrl/Shift additions. Renderer links are not a registered scope.
                    state.SelectedItems.Clear(); added |= state.SelectedBlocks.Add(target.Id);
                }
            }
            state.PropertySelection = targets.LastOrDefault(target => state.SelectedBlocks.Contains(target.Id)) is { } selected
                ? new(selected.Id, null)
                : blocks.LastOrDefault(block => state.SelectedBlocks.Contains(block.Id)) is { } remaining ? new(remaining.Id, null)
                : state.SelectedItems.Count == 0 ? null : state.PropertySelection;
            accepted = true;
            NotifyNativeCompositorSelection(source, "blocks", added);
        }, requiresEdit: false);
        return accepted;
    }
    private void SelectCompositorLinkAfterPending(AssetMetadataDocument source, CompositorEditorState state, CompositorSlot target)
    {
        var renderer = target.Renderer;
        ExecuteCompositorCommand(source, () =>
        {
            if (!compositorEditors.TryGetValue(source, out var current) || !ReferenceEquals(current, state)
                || !source.CompositorBlocks.SelectMany(block => block.Slots).Any(slot => slot == target && ReferenceEquals(slot.Renderer, renderer)))
                throw new InvalidOperationException("The compositor link selection target changed during validation.");
            if (state.SelectedLinks.Contains(target)) return;
            var certificate = CompositorLinkSelectionCertificate.Capture(source, target)
                ?? throw new InvalidOperationException("The compositor link endpoints are no longer canonical.");
            // NodeGraphSelectionBehavior clears the graph scope, not registered list scopes.
            state.SelectedBlocks.Clear(); state.SelectedLinks.Clear(); state.SelectedLinkCertificates.Clear(); state.SelectedLinks.Add(target);
            state.SelectedLinkCertificates.Add(target, certificate);
            // Native links are not property providers, even while a list scope remains selected.
            state.ClearGraphInspectorSelection();
            source.ClearCompositorPresentationRoots();
            NotifyNativeCompositorSelection(source, "blocks");
        }, requiresEdit: false);
    }
    private static bool ApplyCompositorListSelection(CompositorEditorState state, CompositorListItem item,
        IReadOnlyList<CompositorListItem> rows, bool control, bool shift)
    {
        var input = CompositorKeyboardState(state, item.Collection); input.Focus = item.ItemId;
        var anchor = state.AnchorCollection == item.Collection ? state.ListSelectionAnchor : null;
        var first = -1; var last = -1;
        for (var index = 0; index < rows.Count; index++)
        { if (rows[index].ItemId == anchor) first = index; if (rows[index].ItemId == item.ItemId) last = index; }
        var adding = shift && first >= 0 || !control || !state.SelectedItems.Contains(item.ItemId);
        if (adding) RetainCompositorListScope(state, rows);
        if (!control) state.SelectedItems.Clear();
        var added = false;
        if (shift && first >= 0)
        {
            for (var index = Math.Min(first, last); index <= Math.Max(first, last); index++) added |= state.SelectedItems.Add(rows[index].ItemId);
        }
        else
        {
            added = state.SelectedItems.Add(item.ItemId);
            if (!added && control) state.SelectedItems.Remove(item.ItemId);
            state.ListSelectionAnchor = item.ItemId; state.AnchorCollection = item.Collection;
        }
        state.PropertySelection = state.SelectedItems.Contains(item.ItemId) ? new(null, item.ItemId)
            : rows.LastOrDefault(row => state.SelectedItems.Contains(row.ItemId)) is { } selected ? new(null, selected.ItemId) : null;
        return added;
    }
    private static void PrepareCompositorKeyboardRow(CompositorEditorState state, CompositorListItem item)
    {
        var input = CompositorKeyboardState(state, item.Collection);
        if (input.ApplyFocus && input.Focus == item.ItemId) Gui.SetKeyboardFocusHere();
    }
    private void ObserveCompositorKeyboardRow(AssetMetadataDocument source, CompositorEditorState state, CompositorListItem item)
    {
        var input = CompositorKeyboardState(state, item.Collection);
        if (Gui.IsItemFocused())
        {
            if (!input.ApplyFocus && input.Focus != item.ItemId && !Gui.IsMouseDown(ImGuiMouseButton.Left) && !Gui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                var io = Gui.GetIO();
                if (!io.KeyCtrl || io.KeyShift) SelectCompositorListItemAfterPending(source, state, item, io.KeyCtrl && io.KeyShift, io.KeyShift);
            }
            input.Focus = item.ItemId;
        }
        if (input.Focus != item.ItemId) return;
        input.ApplyFocus = false;
        if (!input.ScrollFocus) return;
        var top = Gui.GetWindowPos().Y + Gui.GetStyle().WindowPadding.Y;
        var bottom = Gui.GetWindowPos().Y + Gui.GetWindowSize().Y - Gui.GetStyle().WindowPadding.Y;
        if (Gui.GetItemRectMin().Y < top) Gui.SetScrollHereY(0);
        else if (Gui.GetItemRectMax().Y > bottom) Gui.SetScrollHereY(1);
        input.ScrollFocus = false;
    }
}
