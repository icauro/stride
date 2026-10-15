namespace Stride.Editor.ImGui;

internal enum AssetBrowserDisplayMode { AssetsOnly, AssetsAndFolders, RecursiveAssets }
internal sealed record AssetBrowserContent(AssetEntry? Asset, AssetBrowserFolder? Folder)
{
    public string Id => Asset?.UiId ?? "folder/" + Folder!.Id;
    public string Name => Asset is { } asset ? AssetBrowserNames.Display(asset) : Folder!.Name;
}
internal sealed partial class AssetBrowserState
{
    private AssetBrowserDisplayMode displayMode = AssetBrowserDisplayMode.AssetsAndFolders;
    private readonly List<AssetBrowserContent> visibleContent = [];
    private readonly HashSet<string> selectedContentFolders = new(StringComparer.OrdinalIgnoreCase);
    private string? contentAnchor;
    public string? PrimaryContentFolder { get; private set; }
    public AssetBrowserDisplayMode DisplayMode
    {
        get => displayMode;
        set
        {
            if (displayMode == value) return;
            displayMode = value; IncludeDescendants = value == AssetBrowserDisplayMode.RecursiveAssets;
            dirty = true;
        }
    }
    public IReadOnlyList<AssetBrowserContent> VisibleContent { get { Rebuild(); return visibleContent; } }
    private long contentVersion;
    public long ContentVersion { get { Rebuild(); return contentVersion; } }
    public IReadOnlyList<AssetBrowserFolder> SelectedContentFolders => selectedContentFolders.Select(FindFolder).OfType<AssetBrowserFolder>().ToArray();
    public bool IsContentSelected(AssetBrowserContent content) => content.Asset is { } asset ? IsSelected(asset.Id) : selectedContentFolders.Contains(content.Folder!.Id);
    public string[] CaptureContentFolderSelection() => selectedContentFolders.ToArray();
    public void RestoreContentFolderSelection(IEnumerable<string> ids, string? primary)
    {
        var next = ids.Where(id => FindFolder(id) != null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var nextPrimary = primary != null && next.Contains(primary, StringComparer.OrdinalIgnoreCase) ? primary : next.FirstOrDefault();
        if (selectedContentFolders.SetEquals(next) && string.Equals(PrimaryContentFolder, nextPrimary, StringComparison.OrdinalIgnoreCase)) return;
        using var change = BeforeBrowserSelectionChange();
        selectedContentFolders.Clear();
        foreach (var id in next) selectedContentFolders.Add(id);
        PrimaryContentFolder = primary != null && selectedContentFolders.Contains(primary) ? primary : selectedContentFolders.FirstOrDefault();
        contentAnchor = PrimaryContentFolder != null ? "folder/" + PrimaryContentFolder : PrimaryEntry?.UiId;
        SelectionVersion++; dirty = true;
    }
    public void SelectAllContent(IReadOnlyList<AssetBrowserContent>? scope = null)
    {
        Rebuild();
        var rows = ContentSelectionRows(scope).ToArray(); var previousPrimary = Primary; var previousFolder = PrimaryContentFolder;
        var nextAssets = rows.Where(row => row.Asset != null).Select(row => row.Asset!.Id).ToArray();
        var nextFolders = rows.Where(row => row.Folder != null).Select(row => row.Folder!.Id).ToArray();
        var nextPrimary = previousPrimary is { } oldPrimary && nextAssets.Contains(oldPrimary) ? previousPrimary : rows.FirstOrDefault(row => row.Asset != null)?.Asset?.Id;
        var nextFolder = previousFolder != null && nextFolders.Contains(previousFolder, StringComparer.OrdinalIgnoreCase) ? previousFolder
            : nextPrimary == null ? rows.FirstOrDefault(row => row.Folder != null)?.Folder?.Id : null;
        if (SameBrowserSelection(nextAssets, nextFolders, nextPrimary, nextFolder)
            && selected.SequenceEqual(nextAssets) && rows.Select(row => row.Id).SequenceEqual(CaptureContentSelectionOrder())) return;
        using var change = BeforeBrowserSelectionChange();
        selected.Clear(); selectedContentFolders.Clear(); ClearContentSelectionOrder();
        foreach (var row in rows)
            if (row.Asset is { } asset) selected.Add(asset.Id);
            else if (row.Folder is { } folder) selectedContentFolders.Add(folder.Id);
        foreach (var row in rows) RecordSelectedContent(row);
        Primary = previousPrimary is { } id && selected.Contains(id) ? id : rows.FirstOrDefault(row => row.Asset != null)?.Asset?.Id;
        PrimaryContentFolder = previousFolder != null && selectedContentFolders.Contains(previousFolder) ? previousFolder
            : Primary == null ? rows.FirstOrDefault(row => row.Folder != null)?.Folder?.Id : null;
        if (contentAnchor == null || !rows.Any(row => row.Id == contentAnchor)) contentAnchor = rows.FirstOrDefault()?.Id;
        if (anchor is not { } previousAnchor || !selected.Contains(previousAnchor)) anchor = Primary;
        HasSelectionInteraction = true; SelectionVersion++; selectedEntriesVersion = -1; dirty = true;
    }
    public void SelectContent(AssetBrowserContent content, bool toggle, bool range, IReadOnlyList<AssetBrowserContent>? scope = null)
    {
        Rebuild();
        var rows = ContentSelectionRows(scope);
        if (scope != null && !rows.Any(row => row.Id == content.Id)) return;
        var nextAssets = toggle ? selected.ToHashSet() : new HashSet<Stride.Core.Assets.AssetId>();
        var nextFolders = toggle ? selectedContentFolders.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstRow = range ? rows.FindIndex(row => row.Id == contentAnchor) : -1;
        var lastRow = rows.FindIndex(row => row.Id == content.Id);
        void Prepare(AssetBrowserContent row, bool remove)
        {
            if (row.Asset is { } preparedAsset) { if (!remove || !nextAssets.Remove(preparedAsset.Id)) nextAssets.Add(preparedAsset.Id); }
            else { var folderId = row.Folder!.Id; if (!remove || !nextFolders.Remove(folderId)) nextFolders.Add(folderId); }
        }
        if (range && firstRow >= 0 && lastRow >= 0)
            for (var index = Math.Min(firstRow, lastRow); index <= Math.Max(firstRow, lastRow); index++) Prepare(rows[index], false);
        else Prepare(content, toggle);
        var nextPrimary = content.Asset is { } clickedAsset && nextAssets.Contains(clickedAsset.Id) ? clickedAsset.Id : nextAssets.Count > 0 ? nextAssets.Last() : (Stride.Core.Assets.AssetId?)null;
        var nextFolder = content.Folder is { } clickedFolder && nextFolders.Contains(clickedFolder.Id) ? clickedFolder.Id : nextFolders.LastOrDefault();
        var nextOrder = range && firstRow >= 0 && lastRow >= 0
            ? rows.Skip(Math.Min(firstRow, lastRow)).Take(Math.Abs(lastRow - firstRow) + 1).Select(row => row.Id)
            : new[] { content.Id };
        if (SameBrowserSelection(nextAssets, nextFolders, nextPrimary, nextFolder)
            && (toggle || selected.SequenceEqual(nextAssets) && nextOrder.SequenceEqual(CaptureContentSelectionOrder())))
        { if (!range) contentAnchor = content.Id; return; }
        using var change = BeforeBrowserSelectionChange();
        if (!toggle) { selected.Clear(); selectedContentFolders.Clear(); ClearContentSelectionOrder(); }
        var first = range ? rows.FindIndex(row => row.Id == contentAnchor) : -1;
        var last = rows.FindIndex(row => row.Id == content.Id);
        if (range && first >= 0 && last >= 0)
        {
            for (var index = Math.Min(first, last); index <= Math.Max(first, last); index++) Add(rows[index], false);
        }
        else { Add(content, toggle); contentAnchor = content.Id; }
        Primary = content.Asset is { } asset && selected.Contains(asset.Id) ? asset.Id : selected.Count > 0 ? selected.Last() : null;
        PrimaryContentFolder = content.Folder is { } folder && selectedContentFolders.Contains(folder.Id) ? folder.Id : selectedContentFolders.LastOrDefault();
        if (content.Asset != null) anchor = content.Asset.Id;
        HasSelectionInteraction = true; SelectionVersion++; selectedEntriesVersion = -1; dirty = true;
        void Add(AssetBrowserContent row, bool remove)
        {
            if (row.Asset is { } item) { if (!remove || !selected.Remove(item.Id)) selected.Add(item.Id); }
            else { var id = row.Folder!.Id; if (!remove || !selectedContentFolders.Remove(id)) selectedContentFolders.Add(id); }
            RecordSelectedContent(row);
        }
    }
    private List<AssetBrowserContent> ContentSelectionRows(IReadOnlyList<AssetBrowserContent>? scope)
    {
        if (scope == null) return visibleContent;
        var ids = scope.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        return visibleContent.Where(row => ids.Contains(row.Id)).ToList();
    }

    private void RebuildContent()
    {
        visibleContent.Clear();
        if (displayMode == AssetBrowserDisplayMode.AssetsAndFolders && !includeDescendants)
        {
            var tokens = names.Where(name => name.Enabled).SelectMany(name => name.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            var folders = SelectedDirectories.SelectMany(folder => folder.Children)
                .DistinctBy(folder => folder.Id).Where(folder => tokens.All(token => folder.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
            visibleContent.AddRange(folders.OrderBy(folder => folder.Name, Comparer<string>.Create(NaturalCompare)).Select(folder => new AssetBrowserContent(null, folder)));
        }
        visibleContent.AddRange(visible.Select(asset => new AssetBrowserContent(asset, null)));
        var available = visibleContent.Where(row => row.Folder != null).Select(row => row.Folder!.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = selectedContentFolders.Where(id => FindFolder(id) is not { Parent: { } parent } || !IsSelectedDirectory(parent.Id)).ToArray();
        if (removed.Length > 0)
        {
            using var change = BeforeBrowserSelectionChange();
            selectedContentFolders.ExceptWith(removed); SelectionVersion++; selectedEntriesVersion = -1;
        }
        if (PrimaryContentFolder != null && !selectedContentFolders.Contains(PrimaryContentFolder)) PrimaryContentFolder = selectedContentFolders.FirstOrDefault();
    }
    private void ClearContentSelection()
    { selectedContentFolders.Clear(); PrimaryContentFolder = contentAnchor = null; visibleContent.Clear(); ClearContentSelectionOrder(); }
}
