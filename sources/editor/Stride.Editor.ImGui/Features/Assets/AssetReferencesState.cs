using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

/// <summary>Cached dependency rows from the session manager and retained authored graphs.</summary>
internal sealed class AssetReferencesState
{
    private ProjectWorkspace? workspace;
    private readonly Dictionary<IAssetTagDocument, long> revisions = [];
    private readonly Dictionary<AssetId, Asset> closedAssets = [];
    private long sessionVersion, capturedSessionVersion = -1;
    private IReadOnlyDictionary<AssetId, AssetDependencies> snapshot = new Dictionary<AssetId, AssetDependencies>();
    internal IReadOnlyDictionary<AssetId, AssetDependencies> DependencySnapshot => snapshot;
    internal long DependencyVersion { get; private set; }
    private readonly Dictionary<AssetId, AssetEntry> entries = [];
    private long selectionVersion = -1;
    private bool showReferencers;
    private string query = "";
    private string[] tokens = [];
    private AssetEntry[] references = [], referencers = [], visible = [];
    private readonly HashSet<AssetId> selected = [];
    private readonly HashSet<Type> types = new(AssetFilterTypes.Comparer);
    private readonly HashSet<Type> disabledTypes = new(AssetFilterTypes.Comparer);
    private readonly List<AssetTagFilter> tags = [];
    private readonly List<AssetTagFilter> names = [];
    private readonly AssetFilterOrder filterOrder = new();
    private AssetId? anchor;
    private AssetBrowserSort sort = AssetBrowserSort.TypeThenName;
    private AssetSortMetadata? sortMetadata;
    private long sortMetadataVersion = -1;
    public void UpdateSortMetadata(AssetSortMetadata metadata)
    {
        sortMetadata = metadata;
        if (sortMetadataVersion == metadata.Version) return;
        sortMetadataVersion = metadata.Version;
        if (sort is AssetBrowserSort.DirtyThenName or AssetBrowserSort.ModificationDateThenName) RebuildVisible();
    }
    public string TypeCounters { get; private set; } = "";
    public IReadOnlyList<AssetEntry> Visible => visible;
    public AssetEntry[] SelectedEntries => visible.Where(asset => selected.Contains(asset.Id)).ToArray();
    public IReadOnlyList<Type> AvailableTypes { get; private set; } = [];
    public IReadOnlyList<AssetTagFilter> Tags => tags;
    public IReadOnlyList<AssetTagFilter> Names => names;
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
        RebuildVisible();
    }
    public void AddName(string text)
    {
        if (text.Length == 0 || names.Any(name => string.Equals(name.Text, text, StringComparison.OrdinalIgnoreCase))) return;
        names.Insert(0, new AssetTagFilter(text)); filterOrder.Remember("Name", text); RebuildVisible();
    }
    public void ToggleName(AssetTagFilter name) { name.Enabled = !name.Enabled; RebuildVisible(); }
    public void RemoveName(AssetTagFilter name) { names.Remove(name); filterOrder.Remove("Name", name.Text); RebuildVisible(); }
    public int TypeFilterCount => types.Count;
    public AssetBrowserSort Sort { get => sort; set { if (sort != value) { sort = value; RebuildVisible(); } } }
    public bool IsSelected(AssetId id) => selected.Contains(id);
    public bool HasType(Type type) => types.Contains(type);
    public IReadOnlyList<Type> SelectedTypes => types.OrderBy(type => Stride.Core.DisplayAttribute.GetDisplayName(type), StringComparer.OrdinalIgnoreCase).ToArray();
    public bool IsTypeEnabled(Type type) => types.Contains(type) && !disabledTypes.Contains(type);
    public void ToggleType(Type type) { if (!types.Add(type)) { types.Remove(type); disabledTypes.Remove(type); filterOrder.Remove("Type", type.FullName!); } else filterOrder.Remember("Type", type.FullName!); RebuildVisible(); }
    public void ToggleTypeEnabled(Type type) { if (!types.Contains(type)) return; if (!disabledTypes.Add(type)) disabledTypes.Remove(type); RebuildVisible(); }
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
        RebuildVisible();
    }
    private void RefreshAvailableTypes() => AvailableTypes = AssetFilterTypes.Refresh(types, disabledTypes, entries.Values.Select(asset => asset.AssetType));
    public void AddTag(string text)
    {
        if (text.Length == 0 || tags.Any(tag => string.Equals(tag.Text, text, StringComparison.OrdinalIgnoreCase))) return;
        tags.Add(new AssetTagFilter(text)); filterOrder.Remember("Tag", text); RebuildVisible();
    }
    public void ToggleTag(AssetTagFilter tag) { tag.Enabled = !tag.Enabled; RebuildVisible(); }
    public void RemoveTag(AssetTagFilter tag) { tags.Remove(tag); filterOrder.Remove("Tag", tag.Text); RebuildVisible(); }
    public void ClearFilters() { query = ""; tokens = []; types.Clear(); disabledTypes.Clear(); tags.Clear(); names.Clear(); filterOrder.Clear(); RebuildVisible(); }
    public void SelectAllVisible()
    {
        selected.Clear(); selected.UnionWith(visible.Select(asset => asset.Id));
        if (anchor is not { } previous || !selected.Contains(previous)) anchor = visible.FirstOrDefault()?.Id;
        RebuildVisible();
    }
    public void Select(AssetEntry asset, bool toggle, bool range)
    {
        if (range && anchor is { } start)
        {
            var from = Array.FindIndex(visible, entry => entry.Id == start); var to = Array.FindIndex(visible, entry => entry.Id == asset.Id);
            if (!toggle) selected.Clear();
            if (from >= 0 && to >= 0)
                for (var index = Math.Min(from, to); index <= Math.Max(from, to); index++) selected.Add(visible[index].Id);
            else { selected.Add(asset.Id); anchor = asset.Id; }
        }
        else
        {
            if (!toggle) selected.Clear();
            if (!toggle || !selected.Remove(asset.Id)) selected.Add(asset.Id);
            anchor = asset.Id;
        }
        RebuildVisible();
    }
    public bool ShowReferencers
    {
        get => showReferencers;
        set { if (showReferencers != value) { showReferencers = value; RebuildVisible(true); } }
    }
    public void SetQuery(string value)
    {
        if (query == value) return;
        query = value; tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries); RebuildVisible();
    }
    public void Clear()
    {
        if (workspace != null) workspace.Session.AssetDirtyChanged -= OnSessionAssetChanged;
        workspace = null; revisions.Clear(); entries.Clear(); selected.Clear(); anchor = null;
        closedAssets.Clear(); capturedSessionVersion = -1;
        snapshot = new Dictionary<AssetId, AssetDependencies>();
        references = referencers = visible = []; selectionVersion = -1; TypeCounters = "";
        AvailableTypes = [];
    }
    public void Reset() { Clear(); selected.Clear(); anchor = null; query = ""; tokens = []; types.Clear(); disabledTypes.Clear(); tags.Clear(); names.Clear(); filterOrder.Clear(); }
    public void Update(ProjectWorkspace current, EditorDocumentSession documents, AssetBrowserState selection)
    {
        if (!ReferenceEquals(workspace, current))
        {
            Clear(); workspace = current;
            workspace.Session.AssetDirtyChanged += OnSessionAssetChanged;
        }
        var currentSessionVersion = Interlocked.Read(ref sessionVersion);
        var changed = capturedSessionVersion != currentSessionVersion;
        var documentCount = 0;
        foreach (var authoredDocument in documents.AuthoredDocuments)
            if (authoredDocument is IAssetTagDocument document)
            {
                documentCount++;
                if (!revisions.TryGetValue(document, out var revision) || revision != document.EditRevision) changed = true;
            }
        changed |= documentCount != revisions.Count;
        if (changed)
        {
            var retained = documents.AuthoredDocuments.OfType<IAssetTagDocument>().ToHashSet();
            // A document can close after save or discard while the session's original
            // package object still predates that editor. Reload only those closed graphs.
            foreach (var document in revisions.Keys.Where(document => !retained.Contains(document)))
                if (document.AuthoredAsset is SourceCodeAsset)
                    closedAssets[document.AuthoredAsset.Id] = document.AuthoredAsset;
                else if (File.Exists(document.Path)) closedAssets[document.AuthoredAsset.Id] = AssetFileSerializer.Load<Asset>(document.Path).Asset;
            var authored = new Dictionary<AssetId, Asset>(closedAssets);
            var nextRevisions = new Dictionary<IAssetTagDocument, long>();
            foreach (var authoredDocument in documents.AuthoredDocuments)
                if (authoredDocument is IAssetTagDocument document)
                { authored[document.AuthoredAsset.Id] = document.AuthoredAsset; nextRevisions[document] = document.EditRevision; closedAssets.Remove(document.AuthoredAsset.Id); }
            snapshot = current.Session.DependencyManager.CaptureDependencies(authored);
            DependencyVersion++;
            revisions.Clear();
            foreach (var pair in nextRevisions) revisions.Add(pair.Key, pair.Value);
            capturedSessionVersion = currentSessionVersion;
            entries.Clear();
            foreach (var dependencies in snapshot.Values)
            {
                var item = dependencies.Item;
                entries[item.Id] = new AssetEntry(item.FullPath.ToOSPath(), item.UnqualifiedUrl.ToString(),
                    Path.GetExtension(item.FullPath.ToOSPath()), item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray());
            }
        }
        if (!changed && selectionVersion == selection.SelectionVersion) return;
        selectionVersion = selection.SelectionVersion;
        var selected = selection.SelectedEntries.Select(asset => asset.Id).ToHashSet();
        var outgoing = new HashSet<AssetId>();
        var incoming = new HashSet<AssetId>();
        var processed = new HashSet<AssetId>(selected);
        var queue = new Queue<AssetId>(selected);
        // WPF References recursively includes outgoing links, but Referenced by
        // lists direct consumers of each selected asset.
        foreach (var id in selected)
            if (snapshot.TryGetValue(id, out var dependencies))
                foreach (var link in dependencies.LinksIn)
                    if ((link.Type & ContentLinkType.Reference) != 0) incoming.Add(link.Item.Id);
        while (queue.TryDequeue(out var id))
            if (snapshot.TryGetValue(id, out var dependencies))
                foreach (var link in dependencies.LinksOut)
                    if ((link.Type & ContentLinkType.Reference) != 0)
                    { outgoing.Add(link.Item.Id); if (processed.Add(link.Item.Id)) queue.Enqueue(link.Item.Id); }
        references = Rows(outgoing); referencers = Rows(incoming); RebuildVisible(true);
    }
    private void OnSessionAssetChanged(AssetItem item, bool oldValue, bool newValue) => Interlocked.Increment(ref sessionVersion);
    private AssetEntry[] Rows(IEnumerable<AssetId> ids) => ids.Where(entries.ContainsKey).Select(id => entries[id])
        .OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    private void RebuildVisible(bool sourceChanged = false)
    {
        var rows = showReferencers ? referencers : references;
        if (sourceChanged)
        {
            var ids = rows.Select(asset => asset.Id).ToHashSet(); selected.RemoveWhere(id => !ids.Contains(id));
            if (anchor is { } start && !ids.Contains(start)) anchor = null;
            RefreshAvailableTypes();
        }
        var nameTokens = names.Where(name => name.Enabled).SelectMany(name => name.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var activeTypes = types.Where(IsTypeEnabled).ToHashSet(AssetFilterTypes.Comparer);
        var activeTags = tags.Where(tag => tag.Enabled).ToArray();
        visible = rows.Where(asset => selected.Contains(asset.Id) || tokens.All(token => AssetBrowserNames.Display(asset).Contains(token, StringComparison.OrdinalIgnoreCase))
            && nameTokens.All(token => AssetBrowserNames.Display(asset).Contains(token, StringComparison.OrdinalIgnoreCase))
            && (activeTypes.Count == 0 || activeTypes.Contains(asset.AssetType))
            && activeTags.All(filter => asset.Tags.Any(tag => tag.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)))).ToArray();
        Array.Sort(visible, (left, right) =>
        {
            var result = AssetSortMetadata.Compare(sort, left, right, sortMetadata);
            if (result == 0) result = AssetBrowserState.NaturalCompare(AssetBrowserNames.Natural(left), AssetBrowserNames.Natural(right));
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.UiId, right.UiId);
        });
        TypeCounters = string.Join(", ", rows.GroupBy(asset => asset.TypeLabel).Select(group =>
        {
            var count = group.Count(); var name = group.Key;
            if (count != 1) name = name.Equals("Entity", StringComparison.OrdinalIgnoreCase) ? name[..^1] + "ies" : name + "s";
            return count + " " + name;
        }));
    }
}
