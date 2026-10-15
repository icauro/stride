using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Assets.Serializers;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal static class ExternalReloadDocuments
{
    internal static IEditorDocument Create(IEditorDocument current, byte[] bytes, byte[] hash, string? assetNamespace)
    {
        var log = new LoggerResult();
        IEditorDocument next = current switch
        {
            EntityHierarchyDocument hierarchy => hierarchy.CreateExternalGeneration(bytes, hash, assetNamespace, log),
            ProceduralAssetDocument procedural => procedural.CreateExternalGeneration(bytes, hash, assetNamespace, log),
            AssetMetadataDocument metadata => metadata.CreateExternalGeneration(bytes, hash, assetNamespace, log),
            ScriptSourceDocument source => source.CreateExternalGeneration(hash),
            _ => throw new InvalidOperationException("The document has no actual external-file reload workflow.")
        };
        if (!log.HasErrors) return next;
        next.Dispose(); throw new InvalidDataException(log.ToText());
    }
}
internal partial class AssetMetadataDocument
{
    internal IEditorDocument CreateExternalGeneration(byte[] bytes, byte[] hash, string? assetNamespace, LoggerResult log)
    {
        using var stream = new MemoryStream(bytes, false); var loaded = AssetFileSerializer.Load<Asset>(stream, new UFile(Path), log, assetNamespace);
        if (!ExternalAssetTypeIdentity.Matches(AuthoredAsset, loaded.Asset)) throw new InvalidDataException("The external file changed its actual asset identity or type.");
        return AuthoredAsset is Stride.Core.Yaml.IUnloadable || loaded.Asset is Stride.Core.Yaml.IUnloadable
            ? EditorLoadedAssetDocuments.Create(Path, loaded, hash, SessionHistory, isReadOnly)
            : CreateLoadedGeneration(Path, loaded, hash, SessionHistory, isReadOnly);
    }
}
internal abstract partial class EntityHierarchyDocument
{
    internal IEditorDocument CreateExternalGeneration(byte[] bytes, byte[] hash, string? assetNamespace, LoggerResult log)
    {
        using var stream = new MemoryStream(bytes, false); var loaded = AssetFileSerializer.Load<Asset>(stream, new UFile(Path), log, assetNamespace);
        if (!ExternalAssetTypeIdentity.Matches(Asset, loaded.Asset)) throw new InvalidDataException("The external file changed its actual hierarchy identity or type.");
        if (loaded.Asset is Stride.Core.Yaml.IUnloadable) return EditorLoadedAssetDocuments.Create(Path, loaded, hash, SessionHistory, isReadOnly);
        EntityHierarchyDocument next = this is PrefabDocument ? new PrefabDocument(Path, loaded, SessionHistory!, isReadOnly) : new SceneDocument(Path, loaded, SessionHistory!, isReadOnly);
        next.diskHash = hash.ToArray(); next.RestoreSelection(SelectedIds.ToArray(), Selected?.Id); return next;
    }
}
internal sealed partial class ProceduralAssetDocument
{
    internal IEditorDocument CreateExternalGeneration(byte[] bytes, byte[] hash, string? assetNamespace, LoggerResult log)
    {
        using var stream = new MemoryStream(bytes, false); var loaded = AssetFileSerializer.Load<Asset>(stream, new UFile(Path), log, assetNamespace);
        if (!ExternalAssetTypeIdentity.Matches(Asset, loaded.Asset)) throw new InvalidDataException("The external file changed its actual procedural asset identity or type.");
        return EditorLoadedAssetDocuments.Create(Path, loaded, hash, SessionHistory, isReadOnly);
    }
}
internal partial class ScriptSourceDocument
{
    internal ScriptSourceDocument CreateExternalGeneration(byte[] expectedHash)
    {
        var next = sourceItem == null ? new ScriptSourceDocument(Path, !sourceCanSave, SessionHistory) : new AssetScriptSourceDocument(sourceItem, SessionHistory);
        if (next.DiskContentHash.Span.SequenceEqual(expectedHash)) return next;
        next.Dispose(); throw new IOException("The source file changed again before reload completed.");
    }
}
