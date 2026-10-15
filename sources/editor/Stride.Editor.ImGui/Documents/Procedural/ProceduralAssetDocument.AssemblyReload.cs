using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument : IEditorOwnedFileGeneration
{
    void IEditorOwnedFileGeneration.AcceptOwnedSaveBaseline(byte[] hash, bool savingGeneration)
    {
        diskHash = hash.ToArray();
        if (!savingGeneration) savedRevision = -1;
    }
    internal AssetAssemblyReloadSnapshot CaptureAssemblyReload(AssetItem canonicalItem)
    {
        EndEdit();
        var authoredItem = graph?.AuthoredItem ?? item;
        graph?.PrepareForSave(log, authoredItem);
        if (log.HasErrors) throw new InvalidOperationException("The authored procedural metadata cannot be serialized.");
        using var stream = new MemoryStream(); var assetNamespace = canonicalItem.Package.Container?.AssetNamespace;
        AssetFileSerializer.Save(stream, Asset, authoredItem.YamlMetadata, assetNamespace: assetNamespace);
        return new(this, stream.ToArray(), assetNamespace);
    }
    internal ProceduralAssetDocument CreateAssemblyReloadedGeneration(AssetAssemblyReloadSnapshot snapshot, ILogger migrationLog)
    {
        if (!ReferenceEquals(snapshot.Source, this)) throw new InvalidOperationException("The reload snapshot has another owner.");
        using var stream = new MemoryStream(snapshot.Yaml, false);
        var restored = AssetFileSerializer.Load<ProceduralModelAsset>(stream, new UFile(Path), migrationLog, snapshot.Namespace);
        if (restored.Asset.Id != Asset.Id) throw new InvalidOperationException("The reloaded procedural asset changed its actual identity.");
        var next = new ProceduralAssetDocument(Path, restored, diskHash, SessionHistory, isReadOnly);
        next.savedRevision = savedRevision; next.nextRevision = nextRevision + 1; next.revision = next.nextRevision;
        next.savedAssetTags = SavedTags.ToArray();
        return next;
    }
}
