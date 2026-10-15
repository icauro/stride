using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

/// <summary>Retains complete authored assets for metadata and supported asset settings.</summary>
internal partial class AssetMetadataDocument : IAssetTagDocument
{
    private readonly AssetLoadResult<Asset> loaded;
    private readonly ITransactionStack transactions;
    private readonly EditorDocumentHistoryScope historyScope;
    private byte[] diskHash;
    private long revision, nextRevision, savedRevision;
    private string[] savedTags;
    private readonly bool isReadOnly;
    public string Path { get; private set; }
    public Asset AuthoredAsset => loaded.Asset;
    public IReadOnlyList<string> SavedTags => savedTags;
    public long EditRevision => revision;
    public bool CanSave { get; }
    public virtual bool IsDirty => diskHash.Length == 0 || revision != savedRevision || HasExtensionVirtualChange || HasPendingExtensionMutation || HasTextureSettingChange || HasMaterialSettingChange || HasGameSettingChange || HasSpriteSettingChange || HasSkyboxSettingChange || HasSpriteCommonChange || HasFontSettingChange || HasImportSettingChange || HasCompositorSettingChange;
    public bool CanUndo => transactions.CanRollback || HasTextureSettingChange || HasMaterialSettingChange || HasGameSettingChange || HasSpriteSettingChange || HasSkyboxSettingChange || HasSpriteCommonChange || HasFontSettingChange || HasImportSettingChange || HasCompositorSettingChange;
    public bool CanRedo => transactions.CanRollforward;
    public AssetMetadataDocument(string path, EditorSessionHistory? history = null, bool readOnly = false)
        : this(path, LoadMetadataDiskSnapshot(path), history, readOnly) { }
    internal AssetMetadataDocument(string path, AssetLoadResult<Asset> asset, byte[] baseline, EditorSessionHistory? history, bool readOnly)
    {
        isReadOnly = readOnly;
        historyScope = new(this, history, "Edit asset settings"); transactions = historyScope.Stack;
        Path = System.IO.Path.GetFullPath(path);
        loaded = asset;
        savedTags = loaded.Asset.Tags.ToArray();
        diskHash = baseline.ToArray();
        var detector = new UnloadableDetector(); detector.Visit(loaded.Asset);
        CanSave = !readOnly && loaded.Asset is not IUnloadable && loaded.Asset is not SourceCodeAsset;
    }
    public virtual void CommitEdits() { EndExtensionVirtualEdit(); EndTextureSettingEdit(); EndMaterialSettingEdit(); EndGameSettingEdit(); EndSpriteSettingEdit(); EndSkyboxSettingEdit(); EndSpriteCommonEdit(); EndFontSettingEdit(); EndImportSettingEdit(); EndCompositorSettingEdit(); }
    public void SetTags(IReadOnlyList<string> tags)
    {
        var edit = PrepareTags(tags);
        if (edit == null) return;
        try { edit.Apply(); edit.Commit(); }
        catch { edit.Restore(); throw; }
    }
    public PreparedAssetTagsEdit? PrepareTags(IReadOnlyList<string> tags)
    {
        CommitEdits();
        if (!CanSave) throw new InvalidOperationException("This asset can't be edited.");
        var before = AuthoredAsset.Tags.ToArray(); var after = tags.Distinct(StringComparer.Ordinal).ToArray();
        if (before.SequenceEqual(after)) return null;
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        return new PreparedAssetTagsEdit(AuthoredAsset, before, after, beforeRevision, afterRevision, operation =>
        {
            using (CreateDocumentTransaction()) RecordOperation(operation);
            revision = afterRevision;
        }, () => revision = beforeRevision, () => revision = afterRevision);
    }
    public void Undo() { CommitEdits(); if (CanUndo) transactions.Rollback(); }
    public void Redo() { CommitEdits(); if (CanRedo) transactions.Rollforward(); }
    public virtual void Save()
    {
        historyScope.EnsureMaySave();
        CommitEdits();
        if (!CanSave) throw new InvalidOperationException("Saving is disabled because this asset contains unresolved types.");
        if (diskHash.Length == 0)
        {
            if (File.Exists(Path) || Directory.Exists(Path)) throw new IOException("The new asset path is occupied by another file.");
        }
        else if (!SHA256.HashData(File.ReadAllBytes(Path)).SequenceEqual(diskHash)) throw new IOException("The asset changed on disk. Reload it before saving to avoid overwriting external changes.");
        PrepareAssetGraphMetadata();
        PrepareTextureSettingsForSave();
        PrepareMaterialSettingsForSave();
        PrepareGameSettingsForSave();
        PrepareSpriteSettingsForSave();
        PrepareSkyboxSettingsForSave();
        var assetNamespace = (authoringItem?.Package ?? SessionHistory?.HierarchyGraphs.ResolveAssetItem?.Invoke(AuthoredAsset.Id)?.Package)?.Container?.AssetNamespace;
        using var stream = new MemoryStream(); AssetFileSerializer.Save(stream, loaded.Asset, authoringItem?.YamlMetadata ?? skyboxSettingsItem?.YamlMetadata ?? spriteSettingsItem?.YamlMetadata ?? gameSettingsItem?.YamlMetadata ?? materialSettingsItem?.YamlMetadata ?? textureSettingsItem?.YamlMetadata ?? loaded.YamlMetadata, assetNamespace: assetNamespace);
        var bytes = stream.ToArray();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var written = WindowsOwnedAssetFileWriter.Write(Path, diskHash.Length == 0 ? null : diskHash, bytes);
        diskHash = written.ContentHash; savedRevision = revision; savedTags = loaded.Asset.Tags.ToArray();
        historyScope.Saved(diskHash);
        MarkTextureSettingsSaved();
        MarkMaterialSettingsSaved();
        MarkGameSettingsOrientationSaved();
        MarkSkyboxSettingsSaved();
        MarkImportSettingsSaved();
    }
    public void Dispose() { historyScope.EnsureMayDispose(); DisposeAuthoringGraph(); }
    private sealed class UnloadableDetector : AssetVisitorBase
    {
        public bool Found { get; private set; }
        public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
        {
            if (obj is IUnloadable) { Found = true; return; }
            base.VisitObject(obj, descriptor, visitMembers);
        }
    }
}
