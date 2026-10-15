using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Native item modification times with canonical retained-document dirty state.</summary>
internal sealed class AssetSortMetadata
{
    internal readonly record struct Value(bool IsDirty, DateTime ModifiedTime);
    private ProjectWorkspace? workspace;
    private readonly Dictionary<AssetId, Value> values = [];
    private readonly HashSet<AssetId> retainedIds = [];
    private long nativeVersion, capturedNativeVersion = -1;
    public long Version { get; private set; }
    public Value Get(AssetId id) => values.GetValueOrDefault(id);
    public void Clear()
    {
        if (workspace != null) workspace.Session.AssetDirtyChanged -= NativeChanged;
        workspace = null; values.Clear(); retainedIds.Clear(); capturedNativeVersion = -1; Version++;
    }
    private void NativeChanged(AssetItem item, bool before, bool after) => Interlocked.Increment(ref nativeVersion);
    public void Update(ProjectWorkspace current, EditorDocumentSession documents)
    {
        if (!ReferenceEquals(workspace, current))
        { Clear(); workspace = current; current.Session.AssetDirtyChanged += NativeChanged; }
        var nativeChanged = capturedNativeVersion != Interlocked.Read(ref nativeVersion);
        var retained = documents.AuthoredDocuments.OfType<IAssetTagDocument>().ToDictionary(document => document.AuthoredAsset.Id);
        if (nativeChanged || !retainedIds.SetEquals(retained.Keys))
        {
            var ids = new HashSet<AssetId>();
            foreach (var item in current.AssetItems)
            {
                ids.Add(item.Id);
                if (!retained.ContainsKey(item.Id)) Set(item.Id, new(item.IsDirty, item.ModifiedTime));
                else if (!values.ContainsKey(item.Id)) Set(item.Id, new(false, item.ModifiedTime));
            }
            foreach (var id in values.Keys.Where(id => !ids.Contains(id)).ToArray()) { values.Remove(id); Version++; }
            retainedIds.Clear(); retainedIds.UnionWith(retained.Keys);
            capturedNativeVersion = Interlocked.Read(ref nativeVersion);
        }
        foreach (var (id, document) in retained)
        {
            var prior = Get(id);
            var time = document is IEditorAssetGraphDocument graph ? graph.AuthoringGraph.AuthoredItem.ModifiedTime : prior.ModifiedTime;
            if (time < prior.ModifiedTime) time = prior.ModifiedTime;
            // Native AssetItem timestamps the clean-to-dirty transition. Text and
            // legacy document journals require the same transition policy locally.
            if (document.IsDirty && !prior.IsDirty && time <= prior.ModifiedTime) time = DateTime.Now;
            Set(id, new(document.IsDirty, time));
        }
    }
    private void Set(AssetId id, Value value)
    { if (!values.TryGetValue(id, out var prior) || prior != value) { values[id] = value; Version++; } }
    internal static int Compare(AssetBrowserSort sort, AssetEntry left, AssetEntry right, AssetSortMetadata? metadata)
    {
        var first = metadata?.Get(left.Id) ?? default; var second = metadata?.Get(right.Id) ?? default;
        return sort switch
        {
            AssetBrowserSort.TypeThenName => right.TypeOrder.CompareTo(left.TypeOrder),
            AssetBrowserSort.DirtyThenName => second.IsDirty.CompareTo(first.IsDirty),
            AssetBrowserSort.ModificationDateThenName => second.ModifiedTime.CompareTo(first.ModifiedTime),
            _ => 0,
        };
    }
}
