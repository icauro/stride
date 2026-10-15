using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal enum AssetBrowserSort { Name, TypeThenName, DirtyThenName, ModificationDateThenName }
internal sealed class AssetTagFilter(string text)
{
    public string Text { get; } = text;
    public bool Enabled { get; set; } = true;
}

/// <summary>Real asset identities, WPF-compatible filter logic and cached visible rows.</summary>
internal sealed partial class AssetBrowserState
{
    private IReadOnlyList<AssetEntry>? source;
    private IReadOnlyList<AssetEntry>? boundAssets;
    private ProjectWorkspace? boundWorkspace;
    private readonly HashSet<AssetId> revealedAssets = [];
    private readonly List<AssetEntry> visible = [];
    private readonly HashSet<AssetId> selected = [];
    private readonly HashSet<Type> types = new(AssetFilterTypes.Comparer);
    private readonly List<AssetTagFilter> tags = [];
    private readonly List<AssetTagFilter> names = [];
    private readonly AssetFilterOrder filterOrder = new();
    private readonly HashSet<Type> disabledTypes = new(AssetFilterTypes.Comparer);
    public IReadOnlyList<AssetTagFilter> Names => names;
    private readonly Dictionary<AssetId, string[]> authoredTags = [];
    private readonly Dictionary<AssetId, AssetEntry> entriesById = [];
    private AssetEntry[] selectedEntries = [];
    private long selectedEntriesVersion = -1;
    private string query = "";
    private string[] tokens = [];
    private bool dirty = true;
    private AssetId? anchor;
    private AssetBrowserSort sort;
    private AssetSortMetadata? sortMetadata;
    private long sortMetadataVersion = -1;
    public void UpdateSortMetadata(AssetSortMetadata metadata)
    {
        sortMetadata = metadata;
        if (sortMetadataVersion == metadata.Version) return;
        sortMetadataVersion = metadata.Version;
        if (sort is AssetBrowserSort.DirtyThenName or AssetBrowserSort.ModificationDateThenName) dirty = true;
    }
    public IReadOnlyList<AssetEntry> Visible { get { Rebuild(); return visible; } }
    public IReadOnlyList<AssetTagFilter> Tags => tags;
    public IReadOnlyList<Type> AvailableTypes { get; private set; } = [];
    public int SelectedCount => selected.Count;
    public int TypeFilterCount => types.Count;
    public AssetBrowserSort Sort { get => sort; set { if (sort != value) { sort = value; dirty = true; } } }
    public AssetId? Primary { get; private set; }
    public AssetId? RevealTarget { get; private set; }
    public void CompleteReveal() => RevealTarget = null;
    public bool HasSelectionInteraction { get; private set; }
    public long SelectionVersion { get; private set; }
    public long TagsVersion { get; private set; }
    public IReadOnlyList<AssetEntry> SelectedEntries
    {
        get
        {
            if (selectedEntriesVersion != SelectionVersion)
            {
                selectedEntries = selected.Select(id => entriesById.GetValueOrDefault(id)).OfType<AssetEntry>().ToArray();
                selectedEntriesVersion = SelectionVersion;
            }
            return selectedEntries;
        }
    }
    public AssetId[] CaptureSelection() => selected.ToArray();
    public void RestoreSelection(IEnumerable<AssetId> ids, AssetId? primary)
    {
        var next = NormalizeSelectionIds(ids);
        var nextPrimary = primary is { } chosen && next.Contains(chosen) ? primary : next.Length > 0 ? next[0] : (AssetId?)null;
        if (SameBrowserSelection(next, [], nextPrimary, null) && selected.SequenceEqual(next)) return;
        using var change = BeforeBrowserSelectionChange();
        selected.Clear(); ClearContentSelection();
        if (source != null)
        {
            var available = source.Select(asset => asset.Id).ToHashSet();
            selected.UnionWith(next.Where(available.Contains));
            ExpandSelectionScope();
        }
        Primary = primary is { } id && selected.Contains(id) ? id : selected.Count > 0 ? selected.First() : null;
        anchor = Primary; HasSelectionInteraction = true; SelectionVersion++; dirty = true;
    }
    public void UpdateAuthoredTags(AssetId id, IReadOnlyList<string> values)
    {
        var current = authoredTags.GetValueOrDefault(id) ?? entriesById.GetValueOrDefault(id)?.Tags;
        if (current != null && current.SequenceEqual(values)) return;
        authoredTags[id] = values.ToArray(); TagsVersion++; dirty = true;
    }
    public IReadOnlyList<string> GetAuthoredTags(AssetEntry asset) => authoredTags.GetValueOrDefault(asset.Id) ?? asset.Tags;
    public void Bind(ProjectWorkspace? workspace)
    {
        var assets = workspace?.Assets;
        if (ReferenceEquals(boundWorkspace, workspace) && ReferenceEquals(boundAssets, assets)) return;
        if (workspace != null && ReferenceEquals(boundWorkspace, workspace)) { RefreshEntries(workspace); return; }
        using var change = selected.Count > 0 || selectedContentFolders.Count > 0 || SelectedFolder != null || selectedCategoryIds.Count > 0 ? BeforeBrowserSelectionChange() : null;
        boundWorkspace = workspace; boundAssets = assets; revealedAssets.Clear();
        source = workspace == null ? null : RevealedSource(workspace); visible.Clear(); selected.Clear(); anchor = Primary = RevealTarget = null;
        authoredTags.Clear(); ClearContentSelection(); SelectionVersion++;
        entriesById.Clear(); selectedEntries = []; selectedEntriesVersion = -1; TagsVersion++;
        if (source != null) foreach (var asset in source) entriesById[asset.Id] = asset;
        types.Clear(); disabledTypes.Clear(); tags.Clear(); names.Clear(); filterOrder.Clear(); query = ""; tokens = [];
        HasSelectionInteraction = false;
        RefreshAvailableTypes();
        BindFolders(workspace);
        dirty = true;
    }
    public void SetQuery(string value)
    {
        if (query == value) return;
        query = value; tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries); dirty = true;
    }
    public void RefreshEntries(ProjectWorkspace workspace)
    {
        var nextSource = RevealedSource(workspace);
        var nextIds = nextSource.Select(asset => asset.Id).ToHashSet();
        using var change = selected.Any(id => !nextIds.Contains(id)) ? BeforeBrowserSelectionChange() : null;
        var scope = CaptureNavigationScope();
        boundWorkspace = workspace; boundAssets = workspace.Assets;
        source = nextSource; entriesById.Clear();
        foreach (var asset in source) entriesById[asset.Id] = asset;
        selected.RemoveWhere(id => !entriesById.ContainsKey(id));
        foreach (var id in authoredTags.Keys.Where(id => !entriesById.ContainsKey(id)).ToArray()) authoredTags.Remove(id);
        if (Primary is { } primary && !selected.Contains(primary)) Primary = selected.Count > 0 ? selected.First() : null;
        if (RevealTarget is { } reveal && !entriesById.ContainsKey(reveal)) RevealTarget = null;
        SelectionVersion++; selectedEntriesVersion = -1;
        RefreshAvailableTypes();
        BindFolders(workspace);
        RestoreLocationScope(scope);
        dirty = true;
    }
    public AssetEntry? RevealAsset(ProjectWorkspace workspace, AssetId id)
    {
        Bind(workspace);
        var item = workspace.AssetItems.FirstOrDefault(item => item.Id == id);
        if (item == null) return null;
        if (!entriesById.ContainsKey(id))
        {
            revealedAssets.Add(id);
            RefreshEntries(workspace);
        }
        if (assetFolders.TryGetValue(id, out var folder)) SelectFolder(folder);
        RestoreSelection([id], id); RevealTarget = id;
        return entriesById.GetValueOrDefault(id);
    }
    private IReadOnlyList<AssetEntry> RevealedSource(ProjectWorkspace workspace)
    {
        var entries = workspace.Assets.ToList();
        var ids = entries.Select(asset => asset.Id).ToHashSet();
        foreach (var item in workspace.AssetItems)
        {
            if (!ids.Add(item.Id)) continue;
            var path = item.FullPath.ToOSPath();
            entries.Add(new AssetEntry(path, item.UnqualifiedUrl.ToString(), Path.GetExtension(path), item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray()));
        }
        return entries;
    }
    private void RefreshAvailableTypes() => AvailableTypes = AssetFilterTypes.Refresh(types, disabledTypes, source?.Select(asset => asset.AssetType));
    public bool HasType(Type type) => types.Contains(type);
    public void ToggleType(Type type) { if (!types.Add(type)) { types.Remove(type); disabledTypes.Remove(type); filterOrder.Remove("Type", type.FullName!); } else filterOrder.Remember("Type", type.FullName!); dirty = true; }
    public bool IsTypeEnabled(Type type) => !disabledTypes.Contains(type);
    public void ToggleTypeEnabled(Type type) { if (!disabledTypes.Add(type)) disabledTypes.Remove(type); dirty = true; }
    public void CommitFilter(AssetFilterChoice choice, bool replace)
    {
        if (replace)
        {
            switch (choice.Category)
            {
                case AssetFilterCategory.Name: names.Clear(); filterOrder.RemoveCategory("Name"); break;
                case AssetFilterCategory.Type: types.Clear(); disabledTypes.Clear(); filterOrder.RemoveCategory("Type"); break;
                case AssetFilterCategory.Tag: tags.Clear(); filterOrder.RemoveCategory("Tag"); break;
            }
        }
        switch (choice.Category)
        {
            case AssetFilterCategory.Name:
                AddName(choice.Text);
                break;
            case AssetFilterCategory.Type when choice.Type is { } type:
                var existing = types.FirstOrDefault(current => string.Equals(current.FullName, type.FullName, StringComparison.OrdinalIgnoreCase));
                if (existing == null) { types.Add(type); filterOrder.Remember("Type", type.FullName!); }
                // Native type suggestions reuse their own persistent filter instance.
                if (existing == null || AssetFilterTypes.Comparer.Equals(existing, type)) disabledTypes.Remove(type);
                break;
            case AssetFilterCategory.Tag:
                AddTag(choice.Text);
                break;
        }
        dirty = true;
    }
    public void AddName(string text)
    {
        if (text.Length == 0 || names.Any(filter => filter.Text.Equals(text, StringComparison.OrdinalIgnoreCase))) return;
        names.Insert(0, new AssetTagFilter(text)); filterOrder.Remember("Name", text); dirty = true;
    }
    public void ToggleName(AssetTagFilter filter) { filter.Enabled = !filter.Enabled; dirty = true; }
    public void RemoveName(AssetTagFilter filter) { names.Remove(filter); filterOrder.Remove("Name", filter.Text); dirty = true; }
    public Type? FindFilterType(string fullName) => types.FirstOrDefault(type => type.FullName == fullName);
    public AssetFilterPreference[] CaptureFilters() => filterOrder.Arrange(names.Select(filter => new AssetFilterPreference("Name", filter.Text, filter.Enabled))
        .Concat(types.Select(type => new AssetFilterPreference("Type", type.FullName!, IsTypeEnabled(type))))
        .Concat(tags.Select(filter => new AssetFilterPreference("Tag", filter.Text, filter.Enabled))));
    public void RestoreFilters(IEnumerable<AssetFilterPreference> filters)
    {
        ClearFilters(); RefreshAvailableTypes();
        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters.Take(512).Reverse())
        {
            if (string.IsNullOrEmpty(filter.Value) || filter.Value.Length > 4096) continue;
            if (filter.Category == "Name" && restored.Add("Name\0" + filter.Value)) { names.Insert(0, new AssetTagFilter(filter.Value) { Enabled = filter.Enabled }); filterOrder.Remember("Name", filter.Value); }
            else if (filter.Category == "Tag" && restored.Add("Tag\0" + filter.Value)) { tags.Insert(0, new AssetTagFilter(filter.Value) { Enabled = filter.Enabled }); filterOrder.Remember("Tag", filter.Value); }
            else if (filter.Category == "Type" && AvailableTypes.FirstOrDefault(type => AssetFilterTypes.MatchesPreference(type, filter.Value)) is { } type && restored.Add("Type\0" + type.FullName))
            { types.Add(type); if (!filter.Enabled) disabledTypes.Add(type); filterOrder.Remember("Type", type.FullName!); }
        }
        dirty = true;
    }
    public void AddTag(string text)
    {
        if (text.Length == 0 || tags.Any(tag => string.Equals(tag.Text, text, StringComparison.OrdinalIgnoreCase))) return;
        tags.Add(new AssetTagFilter(text)); filterOrder.Remember("Tag", text); dirty = true;
    }
    public void ToggleTag(AssetTagFilter filter) { filter.Enabled = !filter.Enabled; dirty = true; }
    public void RemoveTag(AssetTagFilter filter) { tags.Remove(filter); filterOrder.Remove("Tag", filter.Text); dirty = true; }
    public void ClearFilters() { types.Clear(); disabledTypes.Clear(); tags.Clear(); names.Clear(); filterOrder.Clear(); query = ""; tokens = []; dirty = true; }
    public bool IsSelected(AssetId id) => selected.Contains(id);
    public void Select(AssetEntry entry, bool toggle, bool range)
    {
        Rebuild();
        var next = toggle ? selected.ToHashSet() : new HashSet<AssetId>();
        var firstRow = range && anchor is { } initial ? visible.FindIndex(asset => asset.Id == initial) : -1;
        var lastRow = visible.FindIndex(asset => asset.Id == entry.Id);
        if (range && firstRow >= 0 && lastRow >= 0)
            for (var index = Math.Min(firstRow, lastRow); index <= Math.Max(firstRow, lastRow); index++) next.Add(visible[index].Id);
        else if (!toggle || !next.Remove(entry.Id)) next.Add(entry.Id);
        var nextPrimary = next.Contains(entry.Id) ? entry.Id : visible.LastOrDefault(asset => next.Contains(asset.Id))?.Id;
        var nextOrder = next.Select(id => entriesById.GetValueOrDefault(id)?.UiId ?? entry.UiId);
        if (SameBrowserSelection(next, toggle ? selectedContentFolders : [], nextPrimary, toggle ? PrimaryContentFolder : null)
            && (toggle || selected.SequenceEqual(next) && nextOrder.SequenceEqual(CaptureContentSelectionOrder())))
        { if (!range) anchor = entry.Id; return; }
        using var change = BeforeBrowserSelectionChange();
        if (!toggle) { selectedContentFolders.Clear(); PrimaryContentFolder = null; ClearContentSelectionOrder(); }
        HasSelectionInteraction = true;
        SelectionVersion++;
        Rebuild();
        if (range && anchor is { } start)
        {
            var first = visible.FindIndex(asset => asset.Id == start); var last = visible.FindIndex(asset => asset.Id == entry.Id);
            if (!toggle) selected.Clear();
            if (first >= 0 && last >= 0)
                for (var index = Math.Min(first, last); index <= Math.Max(first, last); index++)
                { selected.Add(visible[index].Id); RecordSelectedContent(new(visible[index], null)); }
            else { selected.Add(entry.Id); anchor = entry.Id; }
        }
        else
        {
            if (!toggle) selected.Clear();
            if (!toggle || !selected.Remove(entry.Id)) selected.Add(entry.Id);
            anchor = entry.Id;
        }
        RecordSelectedContent(new(entry, null));
        Primary = selected.Contains(entry.Id) ? entry.Id : visible.LastOrDefault(asset => selected.Contains(asset.Id))?.Id;
        dirty = true;
    }
    public AssetEntry? PrimaryEntry => Primary is { } id ? entriesById.GetValueOrDefault(id) : null;
    public bool Matches(AssetEntry asset) => tokens.All(token => AssetBrowserNames.Display(asset).Contains(token, StringComparison.OrdinalIgnoreCase))
        && names.Where(filter => filter.Enabled).All(filter => filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(token => AssetBrowserNames.Display(asset).Contains(token, StringComparison.OrdinalIgnoreCase)))
        && (!types.Any(type => !disabledTypes.Contains(type)) || types.Contains(asset.AssetType) && !disabledTypes.Contains(asset.AssetType))
        && tags.Where(tag => tag.Enabled).All(filter => (authoredTags.GetValueOrDefault(asset.Id) ?? asset.Tags).Any(tag => tag.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)));
    private void Rebuild()
    {
        if (!dirty || source == null) return;
        visible.Clear(); visible.AddRange(source.Where(asset => InSelectedFolder(asset) && (selected.Contains(asset.Id) || Matches(asset))));
        visible.Sort((left, right) =>
        {
            var result = AssetSortMetadata.Compare(sort, left, right, sortMetadata);
            if (result == 0) result = NaturalCompare(AssetBrowserNames.Natural(left), AssetBrowserNames.Natural(right));
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.UiId, right.UiId);
        });
        RebuildContent(); contentVersion++; dirty = false;
    }
    internal static int NaturalCompare(string left, string right)
    {
        var a = 0; var b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (left[a] is >= '0' and <= '9' && right[b] is >= '0' and <= '9')
            {
                var endA = a; var endB = b;
                while (endA < left.Length && left[endA] is >= '0' and <= '9') endA++;
                while (endB < right.Length && right[endB] is >= '0' and <= '9') endB++;
                while (a < endA && left[a] == '0') a++;
                while (b < endB && right[b] == '0') b++;
                var result = (endA - a).CompareTo(endB - b);
                if (result == 0) result = left.AsSpan(a, endA - a).CompareTo(right.AsSpan(b, endB - b), StringComparison.Ordinal);
                if (result != 0) return result;
                a = endA; b = endB;
            }
            else
            {
                var endA = a + 1; var endB = b + 1;
                while (endA < left.Length && left[endA] is not (>= '0' and <= '9')) endA++;
                while (endB < right.Length && right[endB] is not (>= '0' and <= '9')) endB++;
                var result = left.AsSpan(a, endA - a).CompareTo(right.AsSpan(b, endB - b), StringComparison.InvariantCultureIgnoreCase);
                if (result != 0) return result;
                a = endA; b = endB;
            }
        }
        return (left.Length - a).CompareTo(right.Length - b);
    }
}
