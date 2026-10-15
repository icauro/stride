using Stride.Assets.Models;
using Stride.Core.Assets;
using Stride.Core.Assets.Tracking;
using Stride.Core.IO;
using Stride.Core.Storage;
using Stride.SpriteStudio.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record SourceReimportAssetRequest(AssetEntry Entry, AssetMetadataDocument Document, Package Package,
        EditorAuthoringState State, IDisposable Lease, long Revision, SourceReimportCertificate Certificate, int? AnimationStack);
    private sealed record SourceReimportRequest(SourceReimportAssetRequest[] Assets);
    private SourceReimportRequest? sourceReimportRequest;
    private bool CanStartSourceReimport => workspace != null && sessionHistory != null && authoring != null && !IsAssetImportUpdating
        && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && projectLoading == null && assetPreviewBuild == null
        && !RecoveryTransitionBusy && !closeRequested && !closePromptOpen && closingDocument == null;
    private bool HasReimportSources(AssetEntry entry)
    {
        if (workspace == null || !workspace.AssetItems.Any(item => item.Id == entry.Id)) return false;
        var item = workspace.FindAsset(entry.Id);
        if (!IsEditableAssetPackage(item.Package) || item.Asset is SourceCodeAsset) return false;
        var asset = authoring?.Find(entry.Id)?.Document is IAssetTagDocument retained ? retained.AuthoredAsset : item.Asset;
        return new SourceFilesCollector().GetSourceFiles(asset).Count != 0;
    }
    private bool CanReimportAsset(AssetEntry entry) => CanStartSourceReimport && HasReimportSources(entry);
    private bool CanReimportAssets(IReadOnlyList<AssetEntry> entries) => CanStartSourceReimport && entries.Any(HasReimportSources);

    private SourceReimportCertificate CaptureSourceReimportCertificate(AssetEntry entry, Asset asset)
    {
        var directory = Path.GetDirectoryName(entry.Path)!;
        string Absolute(UFile source) => Path.GetFullPath(source.IsAbsolute ? source.ToOSPath() : Path.Combine(directory, source.ToOSPath()));
        var collector = new SourceFilesCollector(); var required = collector.GetCompilationInputFiles(asset);
        var sources = collector.GetSourceFiles(asset).Keys.Union(required)
            .Where(source => required.Contains(source) || File.Exists(Absolute(source))).Select(Absolute).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length == 0) throw new InvalidOperationException("The asset has no source file.");
        var nativeMerge = asset is ModelAsset or SkeletonAsset or AnimationAsset or SpriteStudioModelAsset;
        var primary = asset.MainSource;
        if (nativeMerge && (primary == null || string.IsNullOrWhiteSpace(primary.ToString()))) throw new InvalidOperationException("The asset has no source file.");
        var snapshot = AssetCloner.Clone(asset);
        SourceHashesHelper.ConvertPaths(snapshot, new UDirectory(directory), UPathType.Absolute);
        var saved = SourceHashesHelper.GetAllHashes(snapshot).ToDictionary(pair => Absolute(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new(entry.Id, workspace!.FindAsset(entry.Id).Location.ToString(), asset.GetType(), sources,
            primary == null ? "" : Absolute(primary), nativeMerge, asset is not ModelAsset model || model.DeduplicateMaterials, saved);
    }
    private void RequestAssetReimport(AssetEntry entry) => RequestAssetReimports([entry]);
    private void RequestAssetReimports(IReadOnlyList<AssetEntry> entries, bool onlyModified = false)
    {
        if (!CanReimportAssets(entries)) throw new InvalidOperationException("The source assets are not available.");
        documents.CommitAll();
        var prepared = new List<SourceReimportAssetRequest>();
        CancellationTokenSource? cancellation = null;
        try
        {
            foreach (var entry in entries.DistinctBy(entry => entry.Id).Where(HasReimportSources))
            {
                var item = workspace!.FindAsset(entry.Id);
                if (!SamePath(item.FullPath.ToOSPath(), entry.Path)) throw new InvalidOperationException("The source asset moved.");
                var document = GetCanonicalDocument(entry.Path, () => new AssetMetadataDocument(entry.Path, sessionHistory)) as AssetMetadataDocument
                    ?? throw new InvalidOperationException("The source asset has no native metadata merge owner.");
                document.CommitEdits();
                var state = authoring!.Find(entry.Id) ?? throw new InvalidOperationException("The source asset has no canonical owner.");
                if (!document.CanSave || !ReferenceEquals(state.Document, document) || !state.IsPublished)
                    throw new InvalidOperationException("The source asset is no longer editable.");
                var certificate = CaptureSourceReimportCertificate(entry, document.AuthoredAsset);
                prepared.Add(new(entry, document, item.Package, state, state.AcquireView(), document.EditRevision, certificate,
                    document.AuthoredAsset is AnimationAsset animation ? animation.AnimationStack : null));
            }
            if (prepared.Count == 0) return;
            var certificates = prepared.Select(request => request.Certificate).ToArray();
            var assemblies = CaptureAssetImporterAssemblies();
            cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            sourceReimportRequest = new(prepared.ToArray());
            assetImportWorkspace = workspace; assetImportPackage = prepared[0].Package; assetImportCancellation = cancellation;
            Volatile.Write(ref assetImportProgress, null);
            assetImportTask = Task.Run(async () =>
            {
                var batch = await AssetSourceReimportBatch.Prepare(certificates, assemblies, onlyModified, token,
                    update => { if (!token.IsCancellationRequested) Volatile.Write(ref assetImportProgress, update); }).ConfigureAwait(false);
                return batch.Summary with { Reimports = batch.Results };
            }, token);
            BeginAssetImportProgress(true);
        }
        catch
        {
            if (assetImportTask != null && ReferenceEquals(assetImportCancellation, cancellation)) StopAssetImportUpdate();
            else { foreach (var request in prepared) request.Lease.Dispose(); cancellation?.Dispose(); sourceReimportRequest = null; }
            throw;
        }
    }

    private void PublishReimportedAsset(AssetSourceImportResult result)
    {
        var requests = sourceReimportRequest!.Assets;
        var results = result.Reimports ?? throw new InvalidDataException("The source preparation produced no batch certificate.");
        AssetSourceImport.ValidateSources(result.SourceHashes);
        var updates = new List<(SourceReimportAssetRequest Request, Asset? Asset, Dictionary<UFile, ObjectId> Hashes)>();
        foreach (var request in requests)
        {
            if (!request.State.IsPublished || !ReferenceEquals(authoring!.Find(request.Entry.Id), request.State)
                || !ReferenceEquals(request.State.Document, request.Document) || request.Document.EditRevision != request.Revision
                || !SamePath(request.Document.Path, request.Entry.Path) || !request.Document.CanSave
                || !workspace!.Session.Packages.Contains(request.Package) || !IsEditableAssetPackage(request.Package)
                || !ReferenceEquals(workspace.FindAsset(request.Entry.Id).Package, request.Package))
                throw new InvalidOperationException("An asset changed while its source was being imported.");
            if (!results.TryGetValue(request.Entry.Id, out var prepared)) continue;
            Asset? imported = null;
            if (request.Certificate.NativeMerge)
            {
                var candidates = prepared.Assets.Select(item => item.Asset).Where(asset => asset.GetType() == request.Certificate.AssetType);
                if (request.AnimationStack is { } stack) candidates = candidates.OfType<AnimationAsset>().Where(animation => animation.AnimationStack == stack);
                imported = candidates.SingleOrDefault() ?? throw new InvalidDataException("The source no longer contains the selected asset.");
            }
            var hashes = request.Certificate.Sources.ToDictionary(source => new UFile(source), source => prepared.ContentHashes[source]);
            updates.Add((request, imported, hashes));
        }
        if (results.Keys.Any(id => requests.All(request => request.Entry.Id != id))) throw new InvalidDataException("An unexpected source asset was prepared.");
        using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            foreach (var update in updates) update.Request.Document.ApplySourceReimport(update.Asset, update.Hashes);
        });
        capture.Publish(sessionHistory, $"Update {updates.Count} asset(s) from their source(s)");
        ClearModifiedSourceStatus();
    }
    private void ReleaseSourceReimportRequest()
    {
        var request = sourceReimportRequest; sourceReimportRequest = null;
        if (request != null) foreach (var asset in request.Assets) asset.Lease.Dispose();
    }
}
