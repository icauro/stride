using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal static class EditorRecoveryDocuments
{
    public static EditorRecoveryRecord Capture(IEditorDocument document, AssetItem? item)
    {
        if (!document.IsDirty) throw new InvalidOperationException("The document has no authored changes to recover.");
        if (document is ProjectBuildSettingsDocument project) return project.CaptureRecovery();
        if (document is PackageMetadataDocument metadataOwner) return metadataOwner.CaptureRecovery();
        if (document is PackageSettingsDocument settingsOwner) return settingsOwner.CaptureRecovery();
        var package = item?.Package?.FullPath.ToOSPath();
        var location = item?.Location.ToString();
        if (document is ScriptSourceDocument source)
        {
            if (source.GeneratedByAssetId != null) throw new InvalidOperationException("Generated source must be recovered through its authored visual-script producer.");
            return new(source.Path, "source", item?.Id.ToString(), null, package, location, source.DiskContentHash.ToArray(),
                source.CaptureRecoveryBytes(), [], null, source.ProjectPath, source.SourceFolders.ToArray(), item?.SourceFolder?.ToString());
        }
        if (item == null || document is not IAssetTagDocument tagged || item.Id != tagged.AuthoredAsset.Id)
            throw new InvalidOperationException("The recovery document has no canonical package asset owner.");
        byte[] payload; string? assetNamespace; Guid[] selection = []; Guid? primary = null;
        string kind; byte[] baseline;
        switch (document)
        {
            case EntityHierarchyDocument hierarchy:
                var entity = hierarchy.CaptureAssemblyReload(); payload = entity.Yaml; assetNamespace = entity.Namespace;
                selection = entity.Selection.Ids; primary = entity.Selection.PrimaryId;
                kind = hierarchy is PrefabDocument ? "prefab" : "scene"; baseline = hierarchy.DiskContentHash.ToArray(); break;
            case ProceduralAssetDocument procedural:
                var model = procedural.CaptureAssemblyReload(item); payload = model.Yaml; assetNamespace = model.Namespace;
                kind = "procedural"; baseline = procedural.DiskContentHash.ToArray(); break;
            case AssetMetadataDocument metadata:
                payload = metadata.CaptureCompilationAsset(item); assetNamespace = item.Package.Container?.AssetNamespace;
                kind = metadata is UIDocument ? "ui" : metadata is VisualScriptDocument ? "visual-script" : "asset";
                baseline = metadata.DiskContentHash.ToArray(); break;
            default: throw new InvalidOperationException("This document has no canonical authored recovery serializer.");
        }
        return new(document.Path, kind, item.Id.ToString(), assetNamespace, package, location, baseline, payload, selection, primary, SourceFolder: item.SourceFolder?.ToString());
    }
    public static IEditorDocument RestoreGeneration(IEditorDocument current, EditorRecoveryRecord record, LoggerResult log)
    {
        if (!string.Equals(System.IO.Path.GetFullPath(current.Path), System.IO.Path.GetFullPath(record.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The recovery snapshot has another document path.");
        if (current.IsDirty) throw new InvalidOperationException("The current document already has authored changes.");
        IEditorDocument result = current switch
        {
            EntityHierarchyDocument hierarchy => hierarchy.CreateRecoveryGeneration(record, log),
            ProceduralAssetDocument procedural => procedural.CreateRecoveryGeneration(record, log),
            AssetMetadataDocument metadata => metadata.CreateRecoveryGeneration(record, log),
            ScriptSourceDocument source => source.CreateRecoveryGeneration(record),
            _ => throw new InvalidOperationException("This document cannot restore an authored recovery generation.")
        };
        if (log.HasErrors || !result.CanSave)
        {
            result.Dispose();
            throw new InvalidOperationException(log.HasErrors ? log.ToText() : "The recovered document contains unresolved types or is read-only.");
        }
        return result;
    }
    public static AssetLoadResult<Asset> LoadRecoveryAsset(EditorRecoveryRecord record, LoggerResult log)
    {
        if (record.Kind == "source") throw new InvalidOperationException("Sources require the project source topology recovery workflow.");
        using var stream = new MemoryStream(record.Payload, false);
        var restored = AssetFileSerializer.Load<Asset>(stream, new UFile(record.Path), log, record.Namespace);
        if (log.HasErrors || restored.Asset.Id.ToString() != record.AssetId || restored.Asset is SourceCodeAsset)
            throw new InvalidOperationException(log.HasErrors ? log.ToText() : "The recovery asset identity or actual loaded type is invalid.");
        return restored;
    }
}
