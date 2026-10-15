using Hexa.NET.ImGui;
using Stride.Core.Assets;
using System.Runtime.CompilerServices;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class ContentReferenceChoiceCache
    {
        internal WeakReference<IReadOnlyList<AssetItem>>? Source;
        internal WeakReference<IReadOnlyList<ContentReferenceChoice>>? Choices;
        internal long? ProviderVersion;
    }
    private readonly ConditionalWeakTable<Type, ContentReferenceChoiceCache> contentReferenceChoices = new();
    private ContentReferencePicker? contentReferencePicker;
    private ContentReferencePicker ReferencePicker => contentReferencePicker ??= new(new ContentReferencePickerHost(this));
    private void DrawContentReferencePickerDialog()
    {
        contentReferencePicker?.DrawModal();
        foreach (var picker in templateReferencePickers.ToArray())
        {
            picker.DrawModal();
            if (!picker.HasModalRequest) templateReferencePickers.Remove(picker);
        }
    }
    private void ClearContentReferencePicker()
    {
        foreach (var picker in templateReferencePickers.AsEnumerable().Reverse().ToArray())
        { picker.CancelModal(); templateReferencePickers.Remove(picker); }
        contentReferencePicker?.CancelModal();
        contentReferenceChoices.Clear();
    }
    private IReadOnlyList<ContentReferenceChoice> ContentReferenceChoices(Type type)
    {
        var assets = workspace?.AssetItems;
        if (assets == null) return [];
        var cached = contentReferenceChoices.GetValue(type, _ => new());
        var providerVersion = sessionHistory?.Extensions?.Version;
        if (cached.ProviderVersion == providerVersion && cached.Source is { } source && cached.Choices is { } values
            && source.TryGetTarget(out var previous) && ReferenceEquals(previous, assets)
            && values.TryGetTarget(out var previousChoices)) return previousChoices;
        var choices = assets.Where(asset => !asset.IsDeleted && ContentAssetReference.Accepts(type, asset))
            .Select(asset => new ContentReferenceChoice(asset, asset.Location.ToString()))
            .OrderBy(choice => choice.Label, StringComparer.InvariantCultureIgnoreCase).ToArray();
        cached.Source = new(assets); cached.Choices = new(choices); cached.ProviderVersion = providerVersion;
        return choices;
    }

    private void DrawComponentContentReference(ComponentField field)
    {
        Gui.PushID(field.Name);
        if (!BeginDocumentedComponentRow(field)) { Gui.PopID(); return; }
        ReferencePicker.DrawInspector(field.Type, field.Node.Retrieve(), field.AllowsNullObject, value =>
            Execute(() => { document.UpdateComponent(field, value); document.EndComponentEdit(); }), canEdit: document.CanEditComponentValue(field) && field.SupportsEditing);
        Gui.EndTable(); Gui.PopID();
    }


    private void DrawInspectorContentReference(Type type, object? value, bool allowsNull, Action<object?> commit, bool forceAssignment = false, bool highlighted = false)
        => ReferencePicker.DrawInspector(type, value, allowsNull, commit, forceAssignment, highlighted: highlighted);

    private void DrawContentReferenceValue(Type type, object? value, bool allowsNull, Action<object?> commit, bool mixed = false, bool forceAssignment = false, bool canEdit = true)
        => ReferencePicker.DrawInspector(type, value, allowsNull, commit, forceAssignment, canEdit, mixed: mixed);

    private sealed class ContentReferencePickerHost(EditorGame editor, bool templateWizard = false) : IContentReferencePickerHost
    {
        public ProjectWorkspace? Workspace => editor.workspace;
        public bool HasThumbnails => editor.assetThumbnails != null;
        public bool HasDynamicReferenceThumbnail(Type propertyType) => editor.assetThumbnails != null
            && AssetRegistry.CanPropertyHandleAssets(propertyType, out var assetTypes)
            && !assetTypes.All(assetType => editor.assetThumbnails.GetNativeCompiler(assetType)?.IsStatic == true);
        public bool DrawPickupButton() => editor.icons?.Button("open", EditorLocalization.Text("Select an asset", "ToolTip"), 16) == true;
        public bool DrawFetchButton() => editor.icons?.Button("focus", EditorLocalization.Text("Select the referenced asset", "ToolTip"), 16) == true;
        public bool CanFetch(object? value) => !editor.IsAuthoringPipelineUpdating && ContentAssetReference.Read(value) is { } reference
            && editor.workspace?.AssetItems.Any(item => item.Id == reference.Id && !item.IsDeleted) == true;
        public void Fetch(object? value) => editor.FetchContentReferences([value]);
        public PickerContextCapabilities ContextCapabilities(AssetBrowserContent content) => editor.PickerCapabilities(content);
        public Action? CaptureBackgroundLocationExplore() => editor.CaptureAssetLocationExplore();
        public void RunContextAction(AssetBrowserContent content, PickerContextAction action, AssetBrowserState selection, Func<bool> isCurrent, string? name = null, Action<bool>? completed = null)
            => editor.RunPickerContextAction(content, action, selection, isCurrent, name, completed);
        public void DrawPickerDragSource(AssetBrowserContent content, AssetBrowserState browser, Func<bool> isCurrent)
            => editor.DrawPickerAssetDragSource(content, browser, isCurrent);
        public PickerAuthoringCommand? DrawPickerDropTarget(AssetBrowserFolder folder, Func<bool> isCurrent)
            => editor.DrawPickerAssetDropTarget(folder, isCurrent);
        public void RequestPickerSourceImport(string[] files, AssetBrowserFolder folder, AssetBrowserState browser,
            ContentReferenceEditCertificate retention, Func<bool> isCurrent, Action<bool> completed)
            => editor.Execute(() => editor.RequestPickerSourceImport(files, folder, browser, retention, isCurrent, completed));
        public void CancelPickerTransfers(AssetBrowserState browser) => editor.CancelPickerTransfers(browser);
        public bool ContextCommandPending => editor.IsAssetExploreMessagePending || editor.showAssetDeletion || editor.showAssetReferenceDeletion || editor.pendingAssetDeletion != null
            || editor.showExternalAssetSave || editor.pendingExternalAssetPath != null || editor.IsAssetImportUpdating || !editor.assetImportProgressRetired || !templateWizard && editor.pickerCreationScope != null;
        public void DrawSelectableText(string id, string text) => editor.DrawSelectableText(id, text);
        public IReadOnlyList<AssetFilterPreference> SavedPickerFilters => editor.preferences?.Current.AssetFilters ?? [];
        public void SavePickerFilters(IReadOnlyList<AssetFilterPreference> filters) => editor.SavePickerAssetFilters(filters);
        public AssetPickerViewSettings PickerViewSettings => new(editor.preferences?.Current.AssetSort ?? AssetBrowserSort.TypeThenName,
            editor.preferences?.Current.AssetDisplayMode ?? (editor.preferences?.Current.AssetIncludeDescendants == true ? AssetBrowserDisplayMode.RecursiveAssets : AssetBrowserDisplayMode.AssetsAndFolders),
            editor.preferences?.Current.AssetTileThumbnailSize ?? 96);
        public EditorIcons? PickerIcons => editor.icons;
        public AssetSortMetadata PickerSortMetadata => editor.assetSortMetadata;
        public void SyncPickerSortMetadata() { if (editor.workspace != null) editor.assetSortMetadata.Update(editor.workspace, editor.documents); }
        public int SavePickerThumbnailSize(int size) => editor.SaveAssetPickerThumbnailSize(size);
        public void RefreshPickerTileStatus() => editor.UpdateAssetBuildInclusion();
        public PickerTileBadges DrawPickerTileBadges(AssetEntry asset, System.Numerics.Vector2 origin, float thumbnailSize, Func<bool> isCurrent)
        {
            var command = editor.DrawAssetTileBadges(asset, origin, thumbnailSize);
            if (command == null) return new(null, editor.IsAssetTileBadgeHovered(asset, origin, thumbnailSize));
            var workspace = editor.workspace;
            var item = workspace?.AssetItems.FirstOrDefault(item => item.Id == asset.Id && !item.IsDeleted);
            var path = item?.FullPath.ToOSPath();
            return new(() => editor.Execute(() =>
            {
                if (!isCurrent() || !ReferenceEquals(editor.workspace, workspace) || item == null
                    || !ReferenceEquals(workspace!.Session.FindAsset(item.Id), item) || !SamePath(item.FullPath.ToOSPath(), path!))
                    throw new InvalidOperationException("The asset picker target is no longer current.");
                command();
            }), editor.IsAssetTileBadgeHovered(asset, origin, thumbnailSize));
        }
        public void DrawPickerTooltip(AssetBrowserContent content)
        {
            if (content.Asset is { } asset) editor.DrawAssetTooltip(asset);
            else if (content.Folder is { } folder) editor.DrawAssetFolderTooltip(folder);
        }
        public System.Numerics.Vector2 MeasurePickerLabel(string text) => editor.MeasureNativeLabel(text);
        public void PaintPickerLabel(string text, System.Numerics.Vector2 origin, System.Numerics.Vector2 minimum, System.Numerics.Vector2 maximum, uint color)
            => editor.DrawNativeTreeText(text, origin, minimum, maximum, color);
        public void DrawPickerLabel(string text) => editor.DrawNativeLabel(text);
        public bool DrawPickerFolderNode(string name, ImGuiTreeNodeFlags flags, out bool virtualized)
        {
            var size = editor.MeasureNativeLabel(name);
            using var padding = EditorNativeTreeGeometry.RowPadding(size.Y, ref flags);
            virtualized = EditorTreeVirtualizationAdapter.TrySkipRow("##PickerFolder", flags, false, out var open);
            if (virtualized) return open;
            open = ImGuiP.TreeNodeBehavior(Gui.GetID("##PickerFolder"), flags, "");
            var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
            var origin = minimum + new System.Numerics.Vector2(Gui.GetTreeNodeToLabelSpacing(), Math.Max(0, (maximum.Y - minimum.Y - size.Y) / 2));
            editor.DrawNativeTreeText(name, origin, System.Numerics.Vector2.Max(origin, minimum), maximum);
            return open;
        }
        public string AssetPayloadType => AssetDragType;
        public string? InitialFolderId => editor.assetBrowser.SelectedContent.FirstOrDefault(row => row.Asset != null)?.Asset is { } asset
            ? editor.assetBrowser.FolderForAsset(asset.Id)?.Id : null;
        public Func<bool> CaptureSelectionGuard()
        {
            var workspace = editor.workspace; var history = editor.sessionHistory;
            var version = history?.Extensions?.Version;
            var scene = editor.document; var sceneSelection = scene?.SelectionVersion;
            var browserSelection = editor.assetBrowser.CaptureSelection().ToHashSet(); var browserPrimary = editor.assetBrowser.Primary;
            var active = editor.documents.Active?.Document;
            var ui = editor.ownedUIEditor; var uiDocument = ui?.Document; var uiSelection = ui?.Selection.ToHashSet();
            return () => ReferenceEquals(editor.workspace, workspace) && ReferenceEquals(editor.sessionHistory, history)
                && history?.Extensions?.Version == version && !editor.IsAuthoringPipelineUpdating && !editor.IsAssetPreviewUpdating
                && ReferenceEquals(editor.document, scene) && scene?.SelectionVersion == sceneSelection
                && browserSelection.SetEquals(editor.assetBrowser.CaptureSelection()) && editor.assetBrowser.Primary == browserPrimary && ReferenceEquals(editor.documents.Active?.Document, active)
                && ReferenceEquals(editor.ownedUIEditor, ui) && ReferenceEquals(ui?.Document, uiDocument)
                && (uiSelection == null || ui != null && uiSelection.SetEquals(ui.Selection));
        }
        public Func<bool> CaptureReferenceEditGuard(bool includeRevisions = true)
        {
            var certificate = CaptureReferenceEditCertificate();
            return () => certificate.IsCurrent(includeRevisions);
        }
        public ContentReferenceEditCertificate CaptureReferenceEditCertificate(bool retainOwners = false)
        {
            var workspaceIdentity = editor.assetDragIdentity;
            var extensionVersion = editor.sessionHistory?.Extensions?.Version;
            var resources = editor.contentReferenceResources.ToArray();
            foreach (var resource in resources)
            {
                if (!resource.Current()) throw new InvalidOperationException("The reference property resource context changed.");
                resource.Rebaseline();
            }
            var owners = editor.contentReferenceOwners.Select(owner =>
            {
                var state = editor.sessionHistory?.Registry.Find(owner.Path);
                return (Path: owner.Path, AssetId: state?.AssetId, Generation: state?.Generation,
                    Canonical: state?.IsPublished == true && ReferenceEquals(state.Document, owner),
                    Revision: ContentReferenceOwnerRevision(owner), Selection: (owner as EntityHierarchyDocument)?.SelectionVersion);
            }).ToArray();
            var assetContext = editor.IsCurrentAssetPropertyContext && owners.Any(owner => owner.AssetId == editor.assetBrowser.Primary);
            var browserSelection = assetContext ? editor.assetBrowser.CaptureSelection().ToHashSet() : null;
            var browserPrimary = editor.assetBrowser.Primary;
            var uiId = editor.ownedUIEditor?.Document?.Asset.Id;
            var uiSelection = owners.Any(owner => owner.AssetId == uiId) ? editor.ownedUIEditor?.Selection.ToHashSet() : null;
            // Only real rendering owners are certified; unrelated open asset tabs are not mutation targets.
            // Owner guards retain primitive identities. Explicit row resources acquire their original
            // canonical/presenter leases for the modal and every independent Retain operation.
            bool Current(bool includeRevisions) => owners.Length > 0 && editor.workspace != null && editor.assetDragIdentity == workspaceIdentity && !editor.IsAuthoringPipelineUpdating
                && editor.sessionHistory?.Extensions?.Version == extensionVersion
                && resources.All(resource => resource.Current())
                && (browserSelection == null || editor.IsCurrentAssetPropertyContext && editor.assetBrowser.Primary == browserPrimary
                    && browserSelection.SetEquals(editor.assetBrowser.CaptureSelection()))
                && (uiSelection == null || editor.ownedUIEditor?.Document?.Asset.Id == uiId
                    && uiSelection.SetEquals(editor.ownedUIEditor.Selection))
                && owners.All(owner => owner.Canonical && owner.Revision != null
                    && (owner.AssetId is { } id ? editor.sessionHistory?.Registry.Find(id) : editor.sessionHistory?.Registry.Find(owner.Path)) is { IsPublished: true } state
                    && state.Generation == owner.Generation && state.Document.CanSave
                    && (!includeRevisions || ContentReferenceOwnerRevision(state.Document) == owner.Revision)
                    && (state.Document as EntityHierarchyDocument)?.SelectionVersion == owner.Selection);
            IReadOnlyList<IDisposable> AcquireOwnerLeases()
            {
                if (!Current(true)) throw new InvalidOperationException("The reference picker owner changed.");
                var leases = new List<IDisposable>();
                try
                {
                    foreach (var owner in owners)
                    {
                        var state = owner.AssetId is { } id ? editor.sessionHistory?.Registry.Find(id) : editor.sessionHistory?.Registry.Find(owner.Path);
                        if (state?.IsPublished != true || state.Generation != owner.Generation) throw new InvalidOperationException("The reference picker owner changed.");
                        leases.Add(state.AcquireView());
                    }
                    foreach (var resource in resources) leases.Add(resource.Acquire());
                    return leases;
                }
                catch
                {
                    foreach (var lease in leases)
                        try { lease.Dispose(); } catch { editor.packageCommandCleanup.Add(lease.Dispose); }
                    throw;
                }
            }
            return new(Current, () =>
            {
                for (var index = 0; index < owners.Length; index++)
                {
                    var owner = owners[index];
                    var state = owner.AssetId is { } id ? editor.sessionHistory?.Registry.Find(id) : editor.sessionHistory?.Registry.Find(owner.Path);
                    owners[index].Revision = state == null ? null : ContentReferenceOwnerRevision(state.Document);
                }
                foreach (var resource in resources) resource.Rebaseline();
            }, retainOwners ? AcquireOwnerLeases() : null, AcquireOwnerLeases);
        }
        public IReadOnlyList<ContentReferenceChoice> Choices(Type type) => editor.ContentReferenceChoices(type);
        public bool DrawReferenceMenuItem(string label, bool enabled) => editor.DrawNativeMenuItem(label, "", false, enabled);
        public AssetItem? FindReferenceAsset(string url) => editor.workspace?.AssetItems.FirstOrDefault(item => !item.IsDeleted
            && string.Equals(item.Location.ToString(), url, StringComparison.Ordinal));
        public AssetItem? FindReferenceAsset(AssetId id) => editor.workspace?.Session.FindAsset(id) is { IsDeleted: false } item ? item : null;
        public string ReferenceDisplayUrl(AssetItem item)
        {
            var url = item.Location.ToString();
            if (item.Package?.Container is { AssetNamespace: { } assetNamespace } container
                && editor.workspace?.Session.IsAssetNamespaceInScope(assetNamespace) == true)
                return container.Unqualify(url).FullPath;
            return url;
        }
        public AssetEntry? FindReferenceEntry(AssetId id)
        {
            var item = FindReferenceAsset(id);
            if (item == null) return null;
            var existing = editor.workspace?.Assets.FirstOrDefault(entry => entry.Id == id);
            if (existing != null) return existing;
            var path = item.FullPath.ToOSPath();
            return new AssetEntry(path, item.UnqualifiedUrl.ToString(), Path.GetExtension(path), item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray());
        }
        public void RenderThumbnail(AssetEntry entry, System.Numerics.Vector2 position, float size) => editor.DrawAssetThumbnail(entry, position, size);
        public void Assign(Type type, AssetItem? asset, Action<object?> commit) => editor.Execute(() => commit(asset == null ? null : ContentAssetReference.Create(type, asset)));
        public void AssignValue(object? value, Action<object?> commit) => editor.Execute(() => commit(value));
        public bool AssignPicked(Type type, AssetId id, Func<bool> isCurrent, Action<object?> commit) => editor.Services.Commands.TryExecute(() =>
        {
            if (!isCurrent()) throw new InvalidOperationException("The asset picker target is no longer current.");
            var asset = editor.workspace?.AssetItems.FirstOrDefault(item => item.Id == id && !item.IsDeleted);
            if (asset == null || !ContentAssetReference.Accepts(type, asset)) throw new InvalidOperationException("The selected asset is no longer compatible with this property.");
            commit(ContentAssetReference.Create(type, asset));
        });
        public unsafe bool TryResolveDrop(nint data, int size, Type type, out AssetItem? asset)
        {
            asset = null;
            if (data == 0 || size != sizeof(AssetLocationPayload) || editor.workspace == null) return false;
            var payload = *(AssetLocationPayload*)data;
            if (payload.Workspace != editor.assetDragIdentity || !editor.TryGetAssetDragIds(payload, out var ids) || ids.Length != 1) return false;
            asset = editor.workspace.AssetItems.FirstOrDefault(candidate => candidate.Id == ids[0]);
            return asset != null && ContentAssetReference.Accepts(type, asset);
        }
    }
}
