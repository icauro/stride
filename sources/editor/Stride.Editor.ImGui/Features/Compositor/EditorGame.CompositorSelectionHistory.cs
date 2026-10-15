using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private CompositorSelectionSnapshot[] observedCompositorSelections = [];
    private CompositorSelectionSnapshot[] CaptureCompositorSelections()
        => documents.Entries.Select(entry => entry.Document).OfType<AssetMetadataDocument>()
            .Where(document => document.CompositorAsset != null && compositorEditors.TryGetValue(document, out _))
            .Select(document =>
            {
                compositorEditors.TryGetValue(document, out var state);
                var items = document.CompositorItems.ToDictionary(item => item.ItemId);
                var scopes = state!.SelectedItems.Where(items.ContainsKey).GroupBy(id => items[id].Collection)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new CompositorListScopeSelection(group.Key, group.ToArray())).ToArray();
                return new CompositorSelectionSnapshot(document.AuthoredAsset.Id, state.SelectedBlocks.ToArray(), scopes,
                    state.SelectionPrimary?.Block is { } block && state.SelectedBlocks.Contains(block) ? block : null,
                    state.SelectionPrimary?.Item is { } item && state.SelectedItems.Contains(item) ? item : null);
            }).Where(snapshot => snapshot.Blocks.Length != 0 || snapshot.Lists.Length != 0)
            .OrderBy(snapshot => snapshot.AssetId.ToString(), StringComparer.Ordinal).ToArray();

    private bool CompositorSelectionChanged()
    {
        var current = CaptureCompositorSelections();
        return current.Length != observedCompositorSelections.Length
            || current.Zip(observedCompositorSelections).Any(pair => !pair.First.SameSelection(pair.Second));
    }
    private bool CanResolveCompositorSelection(CompositorSelectionSnapshot snapshot)
        => authoring?.Find(snapshot.AssetId) is { IsPublished: true, Document: AssetMetadataDocument { CompositorAsset: not null } owner }
            && documents.Find(owner) != null && compositorEditors.TryGetValue(owner, out _)
            && (snapshot.Blocks.Any(id => owner.CompositorBlocks.Any(block => block.Id == id))
                || snapshot.Lists.Any(scope => scope.Items.Any(id => owner.CompositorItems.Any(item => item.ItemId == id && item.Collection == scope.Scope))));

    private void RestoreCompositorSelections(CompositorSelectionSnapshot[] snapshots)
    {
        // Closing a view unregisters its native scopes. Resolve IDs only against currently
        // registered views and published canonical generations; never reopen an old graph.
        foreach (var entry in documents.Entries)
        {
            if (entry.Document is not AssetMetadataDocument { CompositorAsset: not null } source
                || !compositorEditors.TryGetValue(source, out var state)
                || authoring?.Find(source.AuthoredAsset.Id) is not { IsPublished: true, Document: var current }
                || !ReferenceEquals(current, source)) continue;
            state.SelectedBlocks.Clear(); state.SelectedItems.Clear();
            state.Dragging = null; state.SelectionStart = null; state.PressedBlock = null;
            state.Linking = null; state.LinkingCertificate = null;
            var snapshot = snapshots.FirstOrDefault(value => value.AssetId == source.AuthoredAsset.Id);
            state.PropertySelection = null;
            if (snapshot == null) continue;
            var blocks = source.CompositorBlocks;
            foreach (var id in snapshot.Blocks)
                if (blocks.Any(block => block.Id == id)) state.SelectedBlocks.Add(id);
            var items = source.CompositorItems;
            foreach (var scope in snapshot.Lists)
                foreach (var id in scope.Items)
                    if (items.Any(item => item.ItemId == id && item.Collection == scope.Scope)) state.SelectedItems.Add(id);
            if (snapshot.PrimaryBlock is { } primaryBlock && state.SelectedBlocks.Contains(primaryBlock))
                state.PropertySelection = new(primaryBlock, null);
            else if (snapshot.PrimaryItem is { } primaryItem && state.SelectedItems.Contains(primaryItem))
                state.PropertySelection = new(null, primaryItem);
            else if (state.SelectedBlocks.Count > 0) state.PropertySelection = new(state.SelectedBlocks.Last(), null);
            else if (state.SelectedItems.Count > 0) state.PropertySelection = new(null, state.SelectedItems.Last());
        }
    }
}
