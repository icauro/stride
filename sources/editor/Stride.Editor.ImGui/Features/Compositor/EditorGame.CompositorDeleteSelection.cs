namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action CaptureCompositorListRemoval(AssetMetadataDocument source, CompositorEditorState state,
        string collection, IReadOnlyList<CompositorListItem> selection)
    {
        var captured = selection.ToArray();
        return () =>
        {
            if (!compositorEditors.TryGetValue(source, out var current) || !ReferenceEquals(state, current))
                throw new InvalidOperationException("The compositor selection owner changed.");
            var rows = source.CompositorItems.Where(item => item.Collection == collection).ToArray();
            var selected = rows.Where(item => state.SelectedItems.Contains(item.ItemId)).ToArray();
            if (selected.Length != captured.Length || captured.Any(item => !selected.Any(actual => actual.ItemId == item.ItemId
                && ReferenceEquals(actual.Value, item.Value) && ReferenceEquals(actual.Node, item.Node)) || !source.CanRemoveCompositorItem(item)))
                throw new InvalidOperationException("The compositor list selection changed during validation.");
            if (captured.Length == 0) return;
            source.RemoveCompositorItems(captured);
            state.SelectedItems.ExceptWith(captured.Select(item => item.ItemId));
            state.PropertySelection = null;
            source.ClearCompositorPresentationRoots();
            NotifyNativeCompositorSelection(source, collection);
        };
    }
    private Action CaptureCompositorDeleteSelection(AssetMetadataDocument source, CompositorEditorState state)
    {
        var items = source.CompositorItems.Where(item => state.SelectedItems.Contains(item.ItemId)).ToArray();
        var blocks = source.CompositorBlocks.Where(block => block.Id != Guid.Empty && state.SelectedBlocks.Contains(block.Id)).ToArray();
        var links = state.SelectedLinks.Select(slot => (Slot: slot,
            Certificate: state.SelectedLinkCertificates.GetValueOrDefault(slot))).ToArray();
        return () =>
        {
            if (!compositorEditors.TryGetValue(source, out var current) || !ReferenceEquals(state, current))
                throw new InvalidOperationException("The compositor selection owner changed.");
            var currentItems = source.CompositorItems;
            var currentBlocks = source.CompositorBlocks;
            foreach (var item in items)
                if (!currentItems.Any(candidate => candidate.Collection == item.Collection && candidate.ItemId == item.ItemId
                    && ReferenceEquals(candidate.Value, item.Value) && ReferenceEquals(candidate.Node, item.Node))
                    || !source.CanRemoveCompositorItem(item))
                    throw new InvalidOperationException("The compositor selection item changed during validation.");
            foreach (var block in blocks)
                if (!currentBlocks.Any(candidate => candidate.Id == block.Id && ReferenceEquals(candidate.Value, block.Value)
                    && ReferenceEquals(candidate.Node, block.Node)))
                    throw new InvalidOperationException("The compositor selection renderer changed during validation.");
            var actualLinks = new List<CompositorSlot>();
            foreach (var link in links)
            {
                CompositorSlot[] matches = link.Certificate == null ? [] : currentBlocks.SelectMany(block => block.Slots)
                    .Where(slot => link.Certificate.Matches(source, slot)).ToArray();
                if (matches.Length != 1 || !source.CanEditCompositorSlot(matches[0]))
                    throw new InvalidOperationException("The compositor selection link changed during validation.");
                actualLinks.Add(matches[0]);
            }
            if (items.Length == 0 && blocks.Length == 0 && links.Length == 0) return;
            var history = source.SessionHistory ?? throw new InvalidOperationException("The compositor session is unavailable.");
            using var capture = history.CaptureOperationsAtomic(() =>
            {
                source.RemoveCompositorItems(items);
                source.DeleteCompositorSelection(blocks.Select(block => block.Id), actualLinks);
            });
            capture.Publish(history, "Delete selection");
            state.SelectedItems.ExceptWith(items.Select(item => item.ItemId));
            state.SelectedBlocks.ExceptWith(blocks.Select(block => block.Id));
            state.SelectedLinks.ExceptWith(links.Select(link => link.Slot));
            foreach (var link in links) state.SelectedLinkCertificates.Remove(link.Slot);
            if (state.PropertySelection is { } selection && (selection.Item is { } itemId && items.Any(item => item.ItemId == itemId)
                || selection.Block is { } blockId && blocks.Any(block => block.Id == blockId))) state.PropertySelection = null;
        };
    }
}
