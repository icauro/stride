using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Storage;

namespace Stride.Editor.ImGui;

internal sealed record SourceReimportCertificate(AssetId Id, string AssetUrl, Type AssetType, string[] Sources,
    string PrimarySource, bool NativeMerge, bool DeduplicateMaterials, IReadOnlyDictionary<string, ObjectId> SavedHashes);
internal sealed record PreparedSourceReimportBatch(IReadOnlyDictionary<AssetId, AssetSourceImportResult> Results, AssetSourceImportResult Summary);
internal sealed class SourceReimportBatchException(Exception failure, IReadOnlyList<ILogMessage> messages)
    : Exception(failure.Message, failure)
{
    internal IReadOnlyList<ILogMessage> Messages { get; } = messages;
}


/// <summary>Detached source preparation; owns no package, canonical document or live Quantum node.</summary>
internal static class AssetSourceReimportBatch
{
    internal static async Task<PreparedSourceReimportBatch> Prepare(SourceReimportCertificate[] certificates,
        AssetImportAssemblyContext[] assemblies, bool onlyModified, CancellationToken cancellation, Action<AssetImportWorkerProgress> progress)
    {
        var allSources = certificates.SelectMany(certificate => certificate.Sources).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var baseline = AssetSourceImport.CaptureSources(allSources, cancellation);
        var results = new Dictionary<AssetId, AssetSourceImportResult>();
        var hashes = new Dictionary<string, string>(baseline.SourceHashes, StringComparer.OrdinalIgnoreCase);
        var contents = new Dictionary<string, ObjectId>(baseline.ContentHashes, StringComparer.OrdinalIgnoreCase);
        var log = new LoggerResult("Asset import");
        try
        {
        for (var index = 0; index < certificates.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var certificate = certificates[index];
            if (onlyModified && certificate.Sources.All(source => certificate.SavedHashes.TryGetValue(source, out var old)
                    && baseline.ContentHashes.TryGetValue(source, out var current) && old == current)) continue;
            log.Verbose($"Updating {certificate.AssetUrl}...");
            progress(new() { AssetUrl = certificate.AssetUrl, AssetType = certificate.AssetType.FullName,
                AssetIndex = index + 1, AssetCount = certificates.Length, Message = $"Updating {certificate.AssetUrl}..." });
            var result = certificate.NativeMerge
                ? await AssetSourceImport.Prepare([certificate.PrimarySource], certificate.AssetType, cancellation, true,
                    certificate.DeduplicateMaterials, assemblies, progress: update => progress(new()
                    {
                        File = update.File, Importer = update.Importer, FileIndex = update.FileIndex, FileCount = update.FileCount,
                        Message = update.Message, HasKnownSteps = update.HasKnownSteps, CurrentStep = update.CurrentStep,
                        StepCount = update.StepCount, Completed = update.Completed, AssetUrl = certificate.AssetUrl,
                        AssetType = certificate.AssetType.FullName, AssetIndex = index + 1, AssetCount = certificates.Length,
                    })).ConfigureAwait(false)
                : new AssetSourceImportResult([], new(StringComparer.OrdinalIgnoreCase), new LoggerResult("Asset import"), new(StringComparer.OrdinalIgnoreCase));
            foreach (var source in certificate.Sources)
            {
                if (result.SourceHashes.TryGetValue(source, out var imported) && imported != baseline.SourceHashes[source])
                    throw new IOException("The source file changed during import: " + source);
                result.SourceHashes[source] = baseline.SourceHashes[source]; result.ContentHashes[source] = baseline.ContentHashes[source];
            }
            foreach (var source in result.SourceHashes)
            {
                if (hashes.TryGetValue(source.Key, out var previous) && previous != source.Value)
                    throw new IOException("The source file changed during import: " + source.Key);
                hashes[source.Key] = source.Value;
            }
            foreach (var source in result.ContentHashes) contents[source.Key] = source.Value;
            foreach (var message in result.Log.Messages) log.Log(message);
            results.Add(certificate.Id, result); log.Verbose($"{certificate.AssetUrl} updated");
        }
        cancellation.ThrowIfCancellationRequested(); AssetSourceImport.ValidateSources(hashes);
        log.Info("Update completed...");
        return new(results, new([], hashes, log, contents));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure) { throw new SourceReimportBatchException(failure, log.Messages.ToArray()); }
    }
}
