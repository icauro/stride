using Stride.Assets;
using Stride.Assets.Materials;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private long materialPreviewRevision, pendingMaterialPreviewRevision;
    private long materialPreviewRevisionSequence;
    private readonly Dictionary<AssetId, (AssetMetadataDocument Document, long Version)> materialPreviewDocuments = [];
    private DateTime materialPreviewRefreshTime;
    private CompiledProjectContent? materialCompiledContent;
    private void InvalidateMaterialCompiledDependencies(CompiledProjectContent content, AssetId? compiledAuthoredTexture = null, bool reapplyDirtyTextures = false)
    {
        NotifySpriteCompiledContentInstalled();
        NotifyFontCompiledContentInstalled();
        NotifySkyboxCompiledDependencyChanged(compiledAuthoredTexture);
        materialCompiledContent = content;
        materialPreviewRevisionSequence++;
        InvalidateCompiledAssetThumbnails(compiledAuthoredTexture);
        if (reapplyDirtyTextures)
            foreach (var document in documents.AuthoredDocuments)
                if (document is AssetMetadataDocument { TextureSettings: not null, IsDirty: true } texture)
                    compiledTextureSettingRevisions.Remove(texture);
    }
    private bool MaterialPreviewUsesContent(CompiledProjectContent content)
    {
        if (RetiredThumbnailServicesUseContent(content)) return true;
        if (ReferenceEquals(materialCompiledContent, content) || ReferenceEquals(materialPreview?.SourceContent, content)) return true;
        foreach (var retired in retiredEditorPreviews)
        {
            if (retired.Preview is IMaterialPreviewContentOwner preview && ReferenceEquals(preview.SourceContent, content)) return true;
            if (retired.Preview is ThumbnailGpuResources thumbnail && ReferenceEquals(thumbnail.SourceContent, content)) return true;
            if (retired.Preview is SceneViewportResources scene && ReferenceEquals(scene.SourceContent, content)) return true;
        }
        return false;
    }

    // Reference materials come from the same retained authored documents as the inspector.
    // Cloning protects their values from material generator normalization.
    private sealed class MaterialPreviewAssetFinder(EditorGame editor) : IAssetFinder
    {
        private readonly Dictionary<AssetId, AssetItem> snapshots = [];
        public AssetItem? FindAsset(AssetId assetId)
        {
            if (snapshots.TryGetValue(assetId, out var snapshot)) return snapshot;
            var item = editor.workspace!.AssetItems.FirstOrDefault(candidate => candidate.Id == assetId);
            if (item == null) return null;
            var authored = (editor.documents.Find(item.FullPath.ToOSPath())?.Document as IAssetTagDocument)?.AuthoredAsset ?? item.Asset;
            snapshot = new AssetItem(item.Location, AssetCloner.Clone(authored));
            snapshots.Add(assetId, snapshot); return snapshot;
        }
        public AssetItem? FindAsset(UFile location)
        {
            var item = editor.workspace!.AssetItems.FirstOrDefault(candidate => candidate.Location == location);
            return item == null ? null : FindAsset(item.Id);
        }
        public AssetItem? FindAssetFromProxyObject(object? proxyObject)
        {
            var reference = AttachedReferenceManager.GetAttachedReference(proxyObject);
            return reference == null ? null : FindAsset(reference.Id);
        }
    }
    private long MaterialPreviewRevision()
    {
        var changed = false; var count = 0;
        foreach (var entry in documents.Entries)
        {
            if (entry.Document is not AssetMetadataDocument { MaterialSettings: not null } document) continue;
            count++;
            if (!materialPreviewDocuments.TryGetValue(document.AuthoredAsset.Id, out var snapshot)
                || !ReferenceEquals(snapshot.Document, document) || snapshot.Version != document.MaterialSettingsEditVersion) changed = true;
        }
        if (!changed && count == materialPreviewDocuments.Count) return materialPreviewRevisionSequence;
        materialPreviewDocuments.Clear();
        foreach (var entry in documents.Entries)
            if (entry.Document is AssetMetadataDocument { MaterialSettings: not null } document)
                materialPreviewDocuments[document.AuthoredAsset.Id] = (document, document.MaterialSettingsEditVersion);
        return ++materialPreviewRevisionSequence;
    }
    private MaterialAssetPreview CreateAuthoredMaterialPreview(AssetEntry entry)
    {
        var item = workspace!.FindAsset(entry.Id);
        var asset = (documents.Find(entry.Path)?.Document as AssetMetadataDocument)?.MaterialSettings ?? (MaterialAsset)item.Asset;
        var settings = item.Package.GetGameSettingsAssetOrDefault();
        return MaterialAssetPreview.FromAuthored(this, renderer!, materialCompiledContent ?? projectContent!, asset, new MaterialPreviewAssetFinder(this),
            item.Location.ToString(), settings.GetOrCreate<EditorSettings>().RenderingMode, settings.GetOrCreate<RenderingSettings>(), RetireEditorPreview);
    }
    private void PumpMaterialSettingsUpdate()
    {
        if (materialPreview == null || !CanStartAuthoringPreviewUpdate) return;
        var revision = MaterialPreviewRevision();
        if (revision == materialPreviewRevision) return;
        if (revision != pendingMaterialPreviewRevision)
        { pendingMaterialPreviewRevision = revision; materialPreviewRefreshTime = DateTime.UtcNow.AddMilliseconds(180); return; }
        if (DateTime.UtcNow < materialPreviewRefreshTime) return;
        // A failed generation is logged once; another authored edit allows retry.
        materialPreviewRevision = revision;
        var entry = workspace.Assets.FirstOrDefault(asset => string.Equals(asset.Path, assetPreviewPath, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        Execute(() =>
        {
            var next = CreateAuthoredMaterialPreview(entry);
            try
            {
                next.SetPrimitive(materialPreview.Primitive);
                next.Camera.SetViewAngles(materialPreview.Camera.Yaw, materialPreview.Camera.Pitch);
            }
            catch { RetireEditorPreview(next); throw; }
            RetireEditorPreview(materialPreview); materialPreview = next; reportedMaterialPreviewError = null;
        });
    }
}
