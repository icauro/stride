using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record AssetAssemblyReloadSnapshot(IEditorDocument Source, byte[] Yaml, string? Namespace);

internal partial class AssetMetadataDocument : IEditorOwnedFileGeneration
{
    void IEditorOwnedFileGeneration.AcceptOwnedSaveBaseline(byte[] hash, bool savingGeneration)
    {
        diskHash = hash.ToArray();
        if (!savingGeneration) savedRevision = -1;
    }
    internal AssetAssemblyReloadSnapshot CaptureAssemblyReload(AssetItem canonicalItem)
    {
        CommitEdits();
        PrepareAssetGraphMetadata();
        PrepareTextureSettingsForSave(); PrepareMaterialSettingsForSave(); PrepareGameSettingsForSave();
        PrepareSpriteSettingsForSave(); PrepareSkyboxSettingsForSave();
        var metadata = authoringItem?.YamlMetadata ?? skyboxSettingsItem?.YamlMetadata ?? spriteSettingsItem?.YamlMetadata ?? gameSettingsItem?.YamlMetadata
            ?? materialSettingsItem?.YamlMetadata ?? textureSettingsItem?.YamlMetadata ?? loaded.YamlMetadata;
        using var stream = new MemoryStream();
        var assetNamespace = canonicalItem.Package.Container?.AssetNamespace;
        AssetFileSerializer.Save(stream, AuthoredAsset, metadata, assetNamespace: assetNamespace);
        return new(this, stream.ToArray(), assetNamespace);
    }
    internal IEditorDocument CreateAssemblyReloadedGeneration(AssetAssemblyReloadSnapshot snapshot, ILogger log)
    {
        if (!ReferenceEquals(snapshot.Source, this)) throw new InvalidOperationException("The reload snapshot has another owner.");
        using var stream = new MemoryStream(snapshot.Yaml, false);
        var restored = AssetFileSerializer.Load<Asset>(stream, new UFile(Path), log, snapshot.Namespace);
        if (restored.Asset.Id != AuthoredAsset.Id || restored.Asset is SourceCodeAsset)
            throw new InvalidOperationException("The reloaded asset changed its actual identity or document kind.");
        var rebound = AuthoredAsset is Stride.Core.Yaml.IUnloadable || restored.Asset is Stride.Core.Yaml.IUnloadable
            ? EditorLoadedAssetDocuments.Create(Path, restored, diskHash, SessionHistory, isReadOnly)
            : CreateLoadedGeneration(Path, restored, diskHash, SessionHistory, isReadOnly);
        if (rebound is not AssetMetadataDocument next)
        {
            ((IEditorOwnedFileGeneration)rebound).AcceptOwnedSaveBaseline(diskHash, false);
            return rebound;
        }
        next.savedRevision = savedRevision; next.nextRevision = nextRevision + 1; next.revision = next.nextRevision;
        next.savedTags = savedTags.ToArray();
        next.CopyGameSettingsOrientationProvenanceFrom(this);
        return next;
    }
    protected virtual AssetMetadataDocument CreateLoadedGeneration(string path, AssetLoadResult<Asset> restored, byte[] baseline,
        EditorSessionHistory? history, bool readOnly) => new(path, restored, baseline, history, readOnly);
}
