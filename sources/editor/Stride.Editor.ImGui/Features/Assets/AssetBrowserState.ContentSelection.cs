namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserState
{
    private readonly List<AssetBrowserContent> contentSelectionOrder = [];
    private readonly HashSet<string> contentSelectionKeys = new(StringComparer.Ordinal);
    internal string[] CaptureNativeContentScopeIds()
        => selected.Select(id => "asset/" + id).Concat(selectedContentFolders.Select(id => "folder/" + id)).ToArray();

    internal IReadOnlyList<AssetBrowserContent> SelectedContent
    {
        get
        {
            for (var index = contentSelectionOrder.Count - 1; index >= 0; index--)
                if (!IsContentSelected(contentSelectionOrder[index]))
                { contentSelectionKeys.Remove(contentSelectionOrder[index].Id); contentSelectionOrder.RemoveAt(index); }
            foreach (var asset in SelectedEntries) RecordSelectedContent(new(asset, null));
            foreach (var folder in SelectedContentFolders) RecordSelectedContent(new(null, folder));
            return contentSelectionOrder.Select(row => row.Asset is { } asset
                ? entriesById.TryGetValue(asset.Id, out var current) ? new AssetBrowserContent(current, null) : null
                : FindFolder(row.Folder!.Id) is { } folder ? new AssetBrowserContent(null, folder) : null)
                .OfType<AssetBrowserContent>().ToArray();
        }
    }

    private void RecordSelectedContent(AssetBrowserContent row)
    {
        if (IsContentSelected(row))
        {
            if (contentSelectionKeys.Add(row.Id)) contentSelectionOrder.Add(row);
        }
        else if (contentSelectionKeys.Remove(row.Id)) contentSelectionOrder.RemoveAll(selected => selected.Id == row.Id);
    }
    internal string[] CaptureContentSelectionOrder() => SelectedContent.Select(row => row.Id).ToArray();
    internal void RestoreContentSelectionOrder(IEnumerable<string> ids)
    {
        var current = SelectedContent.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var next = new List<AssetBrowserContent>();
        foreach (var id in ids) if (current.Remove(id, out var row)) next.Add(row);
        next.AddRange(current.Values);
        if (next.Select(row => row.Id).SequenceEqual(contentSelectionOrder.Select(row => row.Id))) return;
        using var change = BeforeBrowserSelectionChange();
        ClearContentSelectionOrder();
        foreach (var row in next) RecordSelectedContent(row);
    }
    private void ClearContentSelectionOrder() { contentSelectionOrder.Clear(); contentSelectionKeys.Clear(); }
}
