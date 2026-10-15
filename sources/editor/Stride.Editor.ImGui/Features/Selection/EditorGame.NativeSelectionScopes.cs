using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly EditorNativeSelectionScopes nativeSelectionScopes = new();
    private readonly Dictionary<EntityHierarchyDocument, Action> nativeHierarchySelectionEvents = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AssetMetadataDocument, Action<bool>> nativeSpriteSelectionEvents = new(ReferenceEqualityComparer.Instance);
    private bool refreshingNativeSelectionScopes;
    private static string NativeScope(string kind, AssetId id, string collection = "") => kind + "/" + id + "/" + collection;
    private void BeforeNativeSelectionChange()
    { RefreshNativeSelectionScopes(); ValidatePendingPropertyStrings(); }
    private void RefreshNativeSelectionScopes()
    {
        if (refreshingNativeSelectionScopes || workspace == null || authoring == null) return;
        refreshingNativeSelectionScopes = true;
        try
        {
            var retained = new HashSet<string>(StringComparer.Ordinal);
            var published = authoring.PublishedStates.Select(state => state.Document).ToHashSet(ReferenceEqualityComparer.Instance);
            var current = documents.Entries.Select(entry => entry.Document).Where(published.Contains).ToArray();
            Register("assets", assetBrowser, assetBrowser.CaptureNativeContentScopeIds, () => SelectionService.ClearAssetSelection());
            var presented = SelectionService.NativePresentedOwners.ToArray();
            var root = PresentedHierarchyDocument;
            IEnumerable<EntityHierarchyDocument> activeRoot = root == null ? [] : [root];
            var editors = current.OfType<EntityHierarchyDocument>().Concat(activeRoot).Distinct().ToArray();
            var hierarchies = editors.SelectMany(NativeHierarchyMembers).Concat(presented).Distinct().ToArray();
            foreach (var editor in editors)
            {
                var members = NativeHierarchyMembers(editor);
                Register(NativeScope("hierarchy", editor.Asset.Id), editor,
                    () => ReferenceEquals(editor, PresentedHierarchyDocument) ? CapturePresentedNativeSelection()
                        : members.SelectMany(source => source.SelectedIds.Select(id => source.Asset.Id + "/entity/" + id)).ToArray(),
                    () =>
                    {
                        if (ReferenceEquals(editor, PresentedHierarchyDocument))
                        { SelectionService.ClearPresentedHierarchies(); SelectionService.PrimaryIdentity = null; }
                        foreach (var member in members)
                            if (authoring?.Find(member.Asset.Id) is { IsPublished: true, Document: var actual } && ReferenceEquals(actual, member))
                                member.RestoreSelection([], null);
                    }, collections: members.Cast<object>().ToArray());
            }
            foreach (var source in hierarchies)
            {
                if (!nativeHierarchySelectionEvents.ContainsKey(source))
                {
                    Action changed = () => NotifyNativeHierarchySelection(source);
                    nativeHierarchySelectionEvents.Add(source, changed); source.SelectionChanged += changed;
                }
            }
            foreach (var source in nativeHierarchySelectionEvents.Keys.Where(source => !hierarchies.Contains(source)).ToArray())
            { source.SelectionChanged -= nativeHierarchySelectionEvents[source]; nativeHierarchySelectionEvents.Remove(source); }
            foreach (var source in current.OfType<AssetMetadataDocument>())
            {
                if (source.SpriteSettings != null)
                {
                    Register(NativeScope("sprites", source.AuthoredAsset.Id), source,
                        () => source.CaptureNativeSpriteScopeIds().Select(id => id.ToString()).ToArray(), source.ClearNativeSpriteSelection);
                    if (!nativeSpriteSelectionEvents.ContainsKey(source))
                    {
                        Action<bool> changed = added => NotifyNativeSpriteSelection(source, added);
                        nativeSpriteSelectionEvents.Add(source, changed); source.NativeSpriteSelectionChanged += changed;
                    }
                }
                if (source.CompositorAsset != null && compositorEditors.TryGetValue(source, out var state))
                {
                    Register(NativeScope("compositor", source.AuthoredAsset.Id, "blocks"), state,
                        () => state.SelectedBlocks.Select(id => id.ToString()).ToArray(), () =>
                        { state.SelectedBlocks.Clear(); if (state.PropertySelection?.Block != null) state.PropertySelection = null; }, source);
                    foreach (var collection in new[] { "RenderStages", "RenderFeatures", "Cameras" })
                    {
                        var name = collection;
                        Register(NativeScope("compositor", source.AuthoredAsset.Id, name), state,
                            () => source.CompositorItems.Where(item => item.Collection == name && state.SelectedItems.Contains(item.ItemId))
                                .Select(item => item.ItemId.ToString()).ToArray(), () =>
                            {
                                var ids = source.CompositorItems.Where(item => item.Collection == name).Select(item => item.ItemId).ToHashSet();
                                state.SelectedItems.ExceptWith(ids);
                                if (state.PropertySelection?.Item is { } item && ids.Contains(item)) state.PropertySelection = null;
                            }, source);
                    }
                }
            }
            foreach (var source in nativeSpriteSelectionEvents.Keys.Where(source => !current.Contains(source) || source.SpriteSettings == null).ToArray())
            { source.NativeSpriteSelectionChanged -= nativeSpriteSelectionEvents[source]; nativeSpriteSelectionEvents.Remove(source); }
            foreach (var source in current.OfType<UIDocument>())
                if (ownedUIEditors.TryGetValue(source, out var panel) && panel.HasNativeSelectionScope(source))
                    Register(NativeScope("ui", source.Asset.Id), source,
                        () => panel.CaptureNativeSelection(source).Select(id => id.ToString()).ToArray(), () => panel.RestoreNativeSelection(source, []));
            nativeSelectionScopes.Retain(retained);
            void Register(string id, object owner, Func<string[]> capture, Action clear, IEditorDocument? documentOwner = null, object[]? collections = null)
            {
                var owned = documentOwner ?? owner as IEditorDocument;
                bool Current() => workspace != null && authoring != null && (owned == null
                    || authoring.PublishedStates.Any(state => ReferenceEquals(state.Document, owned))
                        && (documents.Find(owned) != null || owned is EntityHierarchyDocument hierarchy && SelectionService.NativePresentedOwners.Contains(hierarchy)));
                retained.Add(id);
                nativeSelectionScopes.Register(id, owner, () => Current() ? capture() : [], () => { if (Current()) clear(); }, collections: collections);
            }
        }
        finally { refreshingNativeSelectionScopes = false; }
    }
    private string[] CapturePresentedNativeSelection() => SelectionService.CaptureNativeHierarchyScopeIds();
    private EntityHierarchyDocument[] NativeHierarchyMembers(EntityHierarchyDocument editor)
    {
        var members = new List<EntityHierarchyDocument> { editor };
        var visited = new HashSet<AssetId> { editor.Asset.Id };
        for (var index = 0; index < members.Count; index++)
            if (members[index] is SceneDocument scene)
                foreach (var id in scene.Asset.ChildrenIds)
                    if (authoring?.Find(id) is { IsPublished: true, Document: SceneDocument child }
                        && SceneParentId(child.Asset) == scene.Asset.Id && visited.Add(id)) members.Add(child);
        return members.ToArray();
    }
    private void NotifyNativeHierarchySelection(EntityHierarchyDocument? source = null)
    {
        RefreshNativeSelectionScopes();
        var owner = source != null && !SelectionService.NativePresentedOwners.Contains(source) ? source : PresentedHierarchyDocument;
        var id = owner == null ? null : NativeScope("hierarchy", owner.Asset.Id);
        if (id == null || !nativeSelectionScopes.Contains(id)) id = source == null ? null : nativeSelectionScopes.FindCollection(source);
        if (id != null) nativeSelectionScopes.Changed(id, changedCollection: source ?? owner);
    }
    private void NotifyNativeAssetSelection() { RefreshNativeSelectionScopes(); nativeSelectionScopes.Changed("assets"); }
    private void NotifyNativeSpriteSelection(AssetMetadataDocument source, bool added)
    { RefreshNativeSelectionScopes(); nativeSelectionScopes.Changed(NativeScope("sprites", source.AuthoredAsset.Id), added); }
    private void NotifyNativeUISelection(UIDocument? source, bool added = false)
    { RefreshNativeSelectionScopes(); if (source != null) nativeSelectionScopes.Changed(NativeScope("ui", source.Asset.Id), added); }
    private void NotifyNativeCompositorSelection(AssetMetadataDocument source, string collection, bool added = false)
    { RefreshNativeSelectionScopes(); nativeSelectionScopes.Changed(NativeScope("compositor", source.AuthoredAsset.Id, collection), added); }
    private void ClearNativeSelectionScopes()
    {
        foreach (var pair in nativeHierarchySelectionEvents) pair.Key.SelectionChanged -= pair.Value;
        foreach (var pair in nativeSpriteSelectionEvents) pair.Key.NativeSpriteSelectionChanged -= pair.Value;
        nativeHierarchySelectionEvents.Clear(); nativeSpriteSelectionEvents.Clear(); nativeSelectionScopes.Clear();
    }
}
