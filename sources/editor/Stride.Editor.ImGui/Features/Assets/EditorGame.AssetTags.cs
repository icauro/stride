using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class AssetTagDraft { public string Text = ""; }
    private readonly Dictionary<string, AssetTagDraft> assetTagDrafts = new(StringComparer.OrdinalIgnoreCase);
    private AssetTagBatchHistory assetTagBatchHistory = new();
    private bool assetTagBatchCommandContext;
    private long groupedTagSelectionVersion = -1, groupedTagsVersion = -1;
    private KeyValuePair<string, int>[] groupedTags = [];
    private void ClearAssetTagState()
    {
        ClearAssetRelocationState();
        assetTagDrafts.Clear(); assetTagBatchHistory.Clear(); assetTagBatchCommandContext = assetRelocationCommandContext = assetDeletionCommandContext = false;
        groupedTagSelectionVersion = groupedTagsVersion = -1; groupedTags = [];
    }

    private void SyncAuthoredAssetTags()
    {
        UpdateAssetSortMetadata();
        foreach (var authored in documents.AuthoredDocuments)
            if (authored is IAssetTagDocument tagged)
                assetBrowser.UpdateAuthoredTags(tagged.AuthoredAsset.Id, tagged.AuthoredAsset.Tags);
    }

    private void ReleaseAssetTagDocument(IEditorDocument document)
    {
        ReleaseCompositorDocument(document);
        ReleaseSkyboxSettingsDocument(document);
        ReleaseFontSettingsDocument(document);
        if (document is AssetMetadataDocument metadata) RemoveGameSettingDrafts(metadata);
        assetDeletionHistory.RemoveDocument(document);
        assetRelocationHistory.RemoveDocument(document);
        if (document is IAssetTagDocument tagged) assetBrowser.UpdateAuthoredTags(tagged.AuthoredAsset.Id, tagged.SavedTags);
        assetTagBatchHistory.RemoveDocument(document);
    }

    private IAssetTagDocument GetAssetTagDocument(AssetEntry asset, bool activate = true, bool openView = true)
    {
        if (documents.Find(asset.Path)?.Document is IAssetTagDocument retained) return retained;
        var readOnly = workspace!.FindAsset(asset.Path).Package.IsReadOnly;
        var document = (IAssetTagDocument)GetCanonicalDocument(asset.Path, () => typeof(Stride.Assets.Entities.SceneAsset).IsAssignableFrom(asset.AssetType) ? new SceneDocument(asset.Path, sessionHistory, readOnly)
            : typeof(Stride.Assets.Entities.PrefabAsset).IsAssignableFrom(asset.AssetType) ? new PrefabDocument(asset.Path, sessionHistory, readOnly)
            : typeof(ProceduralModelAsset).IsAssignableFrom(asset.AssetType) ? new ProceduralAssetDocument(asset.Path, sessionHistory, readOnly)
            : typeof(Stride.Assets.Scripts.VisualScriptAsset).IsAssignableFrom(asset.AssetType) ? new VisualScriptDocument(asset.Path, sessionHistory!, readOnly)
            : typeof(Stride.UI.Assets.UIAssetBase).IsAssignableFrom(asset.AssetType) ? new UIDocument(asset.Path, sessionHistory!, readOnly)
            : typeof(SourceCodeAsset).IsAssignableFrom(asset.AssetType) ? new AssetScriptSourceDocument(workspace.FindAsset(asset.Path), sessionHistory)
            : new AssetMetadataDocument(asset.Path, sessionHistory, readOnly));
        // Property inspection owns the canonical graph, not an editor tab.
        // Explicit editor opening can publish a view of this same retained owner.
        if (!openView) return document;
        var entry = documents.Add(document);
        try { if (activate) ActivateDocument(entry); }
        catch { documents.Remove(entry); throw; }
        return document;
    }

    private void DrawSelectedAssetTags()
    {
        if (assetBrowser.SelectedCount > 1) { DrawGroupedAssetTags(); return; }
        if (assetBrowser.SelectedCount != 1 || assetBrowser.PrimaryEntry is not { } asset) return;
        var retained = authoring?.Find(asset.Path)?.Document as IAssetTagDocument;
        IReadOnlyList<string> tags = retained != null ? retained.AuthoredAsset.Tags : assetBrowser.GetAuthoredTags(asset);
        var canEdit = !workspace!.FindAsset(asset.Path).Package.IsReadOnly
            && (retained?.CanSave ?? true);
        DrawAssetTags(asset.Path, tags, canEdit, values =>
        {
            var document = GetAssetTagDocument(asset, activate: false, openView: false);
            document.SetTags(values); assetBrowser.UpdateAuthoredTags(asset.Id, document.AuthoredAsset.Tags);
            packageFolderCommandContext = false;
            assetTagBatchCommandContext = assetRelocationCommandContext = assetDeletionCommandContext = false;
        });
    }

    private void DrawAssetTags(string path, IReadOnlyList<string> tags, bool canEdit, Action<IReadOnlyList<string>> apply)
    {
        canEdit &= !IsAuthoringPipelineUpdating;
        Gui.PushID(path);
        if (!assetTagDrafts.TryGetValue(path, out var draft)) assetTagDrafts.Add(path, draft = new AssetTagDraft());
        Gui.BeginDisabled(!canEdit);
        Gui.SetNextItemWidth(-1);
        if (Gui.InputTextWithHint("##NewAssetTag", "Add new tag", ref draft.Text, 1024, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            var tag = draft.Text;
            if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag, StringComparer.Ordinal)) Execute(() => apply(tags.Append(tag).ToArray()));
            draft.Text = "";
        }
        string? remove = null;
        foreach (var tag in tags)
        {
            Gui.PushID(tag);
            Gui.TextUnformatted(tag); Gui.SameLine();
            if (Gui.SmallButton("×")) remove = tag;
            Gui.PopID();
        }
        if (remove != null) { var value = remove; Execute(() => apply(tags.Where(tag => !string.Equals(tag, value, StringComparison.Ordinal)).ToArray())); }
        Gui.EndDisabled(); Gui.PopID();
    }

    private void DrawAssetMetadataDocument(AssetMetadataDocument document, bool inPropertyGrid = false)
    {
        if (document.AuthoredAsset is Stride.Core.Yaml.IUnloadable unknown)
        {
            DrawUnloadableProperty(unknown);
            return;
        }
        if (inPropertyGrid && document.AuthoredAsset is Stride.Assets.Rendering.GraphicsCompositorAsset
            && authoring?.Find(document.AuthoredAsset.Id) is { IsPublished: true, Document: var currentCompositor }
            && ReferenceEquals(currentCompositor, document)
            && compositorEditors.TryGetValue(document, out var compositorState)
            && compositorState.SelectedLinks.Count != 0 && compositorState.PropertySelection == null) return;
        using var referenceOwners = BeginContentReferenceOwnerScope(document);
        BeginAssetPresenterRows(document);
        DrawAssetRelationshipProperties(document);
        if (document.AuthoredAsset is Stride.Assets.Rendering.GraphicsCompositorAsset)
        {
            if (inPropertyGrid) DrawCompositorSelectedProperties(document);
            else DrawCompositorEditor(document);
            return;
        }
        DrawImportAssetSettings(document);
        DrawTextureSettings(document);
        DrawMaterialSettings(document);
        DrawGameSettings(document);
        DrawSpriteSettings(document, inPropertyGrid);
        if (document.SpriteSettings != null && !inPropertyGrid) return;
        DrawSkyboxSettings(document);
        DrawFontSettings(document);
        DrawExtensionVirtualProperties(document);
        if (inPropertyGrid || !IsPropertyGridAssetTagContext(document)) DrawAssetTags(document.Path, document.AuthoredAsset.Tags, document.CanSave, values =>
        {
            document.SetTags(values); assetBrowser.UpdateAuthoredTags(document.AuthoredAsset.Id, document.AuthoredAsset.Tags);
            packageFolderCommandContext = false;
            assetTagBatchCommandContext = assetRelocationCommandContext = assetDeletionCommandContext = false;
        });
    }

    private void DrawGroupedAssetTags()
    {
        var selected = assetBrowser.SelectedEntries;
        if (groupedTagSelectionVersion != assetBrowser.SelectionVersion || groupedTagsVersion != assetBrowser.TagsVersion)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var asset in selected)
                foreach (var tag in assetBrowser.GetAuthoredTags(asset).Distinct(StringComparer.Ordinal)) counts[tag] = counts.GetValueOrDefault(tag) + 1;
            groupedTags = counts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            groupedTagSelectionVersion = assetBrowser.SelectionVersion; groupedTagsVersion = assetBrowser.TagsVersion;
        }
        var canEdit = selected.All(asset => !workspace!.FindAsset(asset.Path).Package.IsReadOnly
            && (authoring?.Find(asset.Path)?.Document.CanSave ?? true));
        if (!assetTagDrafts.TryGetValue("##SelectedAssetTags", out var draft)) assetTagDrafts.Add("##SelectedAssetTags", draft = new AssetTagDraft());
        Gui.BeginDisabled(!canEdit); Gui.SetNextItemWidth(-1);
        if (Gui.InputTextWithHint("##NewGroupedTag", "Add new tag", ref draft.Text, 1024, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (!string.IsNullOrWhiteSpace(draft.Text)) { var tag = draft.Text; Execute(() => ApplyGroupedAssetTag(selected, tag, false)); }
            draft.Text = "";
        }
        string? add = null, remove = null;
        foreach (var (tag, count) in groupedTags)
        {
            Gui.PushID(tag); Gui.TextUnformatted(tag); Gui.SameLine();
            if (count == selected.Count) Gui.TextUnformatted("(all)");
            else if (Gui.SmallButton($"({count})")) add = tag;
            Gui.SameLine(); if (Gui.SmallButton("×")) remove = tag; Gui.PopID();
        }
        if (add != null) { var tag = add; Execute(() => ApplyGroupedAssetTag(selected, tag, false)); }
        if (remove != null) { var tag = remove; Execute(() => ApplyGroupedAssetTag(selected, tag, true)); }
        Gui.EndDisabled();
    }

    private void ApplyGroupedAssetTag(IReadOnlyList<AssetEntry> assets, string tag, bool remove)
    {
        // Fully load/check the retained graphs before changing any tag payload.
        var changes = new List<(IAssetTagDocument Document, string[] Tags)>();
        foreach (var asset in assets)
        {
            var document = GetAssetTagDocument(asset, false);
            if (!document.CanSave) throw new InvalidOperationException("This asset can't be edited.");
            var tags = document.AuthoredAsset.Tags;
            var after = remove ? tags.Where(value => !string.Equals(value, tag, StringComparison.Ordinal)).ToArray()
                : tags.Contains(tag, StringComparer.Ordinal) ? tags.ToArray() : tags.Append(tag).ToArray();
            changes.Add((document, after));
        }
        if (assetTagBatchHistory.Apply(changes))
        {
            assetTagBatchCommandContext = true; packageFolderCommandContext = assetRelocationCommandContext = assetDeletionCommandContext = false;
            foreach (var (document, _) in changes) assetBrowser.UpdateAuthoredTags(document.AuthoredAsset.Id, document.AuthoredAsset.Tags);
        }
    }
}
