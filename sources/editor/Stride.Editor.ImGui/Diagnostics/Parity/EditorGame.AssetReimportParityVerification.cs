using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Tracking;
using Stride.Core.IO;
using Stride.Core.Storage;

namespace Stride.Editor.ImGui;

internal sealed class AssetReimportParityRun
{
    internal int Stage, Baseline, Mode;
    internal Guid Transaction;
    internal required string Artifacts;
    internal required ProjectWorkspace Workspace;
    internal required EditorSessionHistory History;
    internal required AssetEntry[] Entries;
    internal required AssetMetadataDocument[] Documents;
    internal required EditorAuthoringState[] Owners;
    internal required IDisposable[] Leases;
    internal required byte[][] OriginalAssetBytes;
    internal required byte[][] OriginalSourceBytes;
    internal required string[] SourcePaths;
    internal required string[] ExpectedSourceSha;
    internal required string[][] OriginalNativeHashes;
    internal required string[] RootReferences;
    internal Task<AssetSourceImportResult>? Producer;
    internal readonly List<object> Receipts = [];
    internal byte[][]? UpdatedAssetBytes;
    internal bool Completed, Released, CloneIsolationVerified;
}

internal sealed partial class EditorGame
{
    private string[]? reimportParityPaths, reimportParitySources;
    private AssetId[]? reimportParityIds;
    internal bool AssetReimportParityReady => CanStartSourceReimport && !IsPackageMetadataSaving;

    internal void PrepareAssetReimportFixtureBeforeLoad(string packagePath)
    {
        var full = Path.GetFullPath(packagePath);
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase) || reimportParityPaths != null)
            throw new InvalidOperationException("Source reimport fixture requires a fresh private package.");
        var root = Path.GetDirectoryName(full)!;
        var suffix = Guid.NewGuid().ToString("N");
        reimportParityPaths = new string[2]; reimportParitySources = new string[2]; reimportParityIds = new AssetId[2];
        var package = AssetFileSerializer.Load<Package>(full).Asset;
        var packageOwner = package.Container ?? new StandalonePackage(package);
        for (var index = 0; index < 2; index++)
        {
            var name = "SourceReimport" + index + "-" + suffix;
            var source = Path.Combine(root, "Resources", name + ".png"); var assetPath = Path.Combine(root, "Assets", name + ".sdtex");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            using (var image = new Image<Rgba32>(32, 32, new Rgba32((byte)(40 + index * 40), 80, 120, 255))) image.SaveAsPng(source);
            var texture = new TextureAsset { Source = new UFile(Path.GetRelativePath(Path.GetDirectoryName(assetPath)!, source).Replace('\\', '/')), IsCompressed = false };
            using (var stream = File.OpenRead(source))
                SourceHashesHelper.UpdateHashes(texture, new Dictionary<UFile, ObjectId> { [new UFile(source)] = AssetSourceImport.Fingerprint(stream, CancellationToken.None).Object });
            SourceHashesHelper.ConvertPaths(texture, new UDirectory(Path.GetDirectoryName(assetPath)!), UPathType.Relative);
            SaveStockNativeFixture(assetPath, texture, name);
            package.RootAssets.Add(new AssetReference(texture.Id, packageOwner.Qualify(new UFile(name))));
            reimportParityPaths[index] = assetPath; reimportParitySources[index] = source; reimportParityIds[index] = texture.Id;
        }
        AssetFileSerializer.Save(full, package, null);
    }

    internal AssetReimportParityRun BeginAssetReimportParityVerification(string artifacts)
    {
        if (!AssetReimportParityReady || workspace == null || authoring == null || sessionHistory == null
            || reimportParityPaths == null || reimportParitySources == null || reimportParityIds == null)
            throw new InvalidOperationException("Prepare and load the private source reimport fixture first.");
        var entries = reimportParityIds.Select(id => workspace.Assets.Single(entry => entry.Id == id)).ToArray();
        var docs = entries.Select(entry => GetAssetTagDocument(entry, false, false) as AssetMetadataDocument
            ?? throw new InvalidOperationException("Texture reimport requires native metadata owners.")).ToArray();
        var owners = entries.Select(entry => authoring.Find(entry.Id) ?? throw new InvalidOperationException("Missing canonical source owner.")).ToArray();
        if (docs.Any(doc => !doc.CanSave || doc.IsDirty)) throw new InvalidOperationException("Source reimport fixtures must start clean.");
        var transactions = sessionHistory.Stack.RetrieveAllTransactions().ToArray();
        var position = sessionHistory.History.Position;
        var assets = docs.Select(doc => doc.AuthoredAsset).ToArray(); var graphs = docs.Select(doc => doc.AuthoringGraph).ToArray();
        var revisions = docs.Select(doc => doc.EditRevision).ToArray();
        foreach (var doc in docs) doc.Save(); // Actual native serialization normalization; no fixture journal.
        ReimportRequire(sessionHistory.History.Position == position && transactions.SequenceEqual(sessionHistory.Stack.RetrieveAllTransactions())
            && docs.Select((doc, index) => ReferenceEquals(doc.AuthoredAsset, assets[index]) && ReferenceEquals(doc.AuthoringGraph, graphs[index])
                && doc.EditRevision == revisions[index] && !doc.IsDirty).All(value => value), "Source fixture native Save changed canonical graph/history identity.");
        var normalizedBytes = entries.Select(entry => File.ReadAllBytes(entry.Path)).ToArray();
        for (var index = 0; index < docs.Length; index++)
        {
            var beforeHashes = ReimportNativeHashes(docs[index].AuthoredAsset, docs[index].Path);
            var detached = AssetCloner.Clone(docs[index].AuthoredAsset);
            SourceHashesHelper.ConvertPaths(detached, new UDirectory(Path.GetDirectoryName(docs[index].Path)!), UPathType.Absolute);
            SourceHashesHelper.UpdateHashes(detached, new Dictionary<UFile, ObjectId>());
            SourceHashesHelper.ConvertPaths(detached, new UDirectory(Path.GetDirectoryName(docs[index].Path)!), UPathType.Relative);
            ReimportRequire(SourceHashesHelper.GetAllHashes(detached).Count == 0
                && ReimportNativeHashes(docs[index].AuthoredAsset, docs[index].Path).SequenceEqual(beforeHashes),
                "Native clone source-hash mutation changed the original canonical asset.");
            docs[index].Save();
            ReimportRequire(File.ReadAllBytes(entries[index].Path).SequenceEqual(normalizedBytes[index]),
                "Detached source-hash mutation changed original serialized collection IDs.");
        }
        var leases = owners.Select(owner => owner.AcquireView()).ToArray();
        return new() { CloneIsolationVerified = true, Artifacts = Path.GetFullPath(artifacts), Workspace = workspace, History = sessionHistory, Entries = entries, Documents = docs, Owners = owners, Leases = leases,
            Baseline = sessionHistory.History.Position, OriginalAssetBytes = entries.Select(entry => File.ReadAllBytes(entry.Path)).ToArray(),
            OriginalSourceBytes = reimportParitySources.Select(File.ReadAllBytes).ToArray(), SourcePaths = reimportParitySources.ToArray(),
            ExpectedSourceSha = reimportParitySources.Select(TemplateFileHash).ToArray(),
            OriginalNativeHashes = docs.Select(doc => ReimportNativeHashes(doc.AuthoredAsset, doc.Path)).ToArray(),
            RootReferences = workspace.Package.RootAssets.Select(reference => reference.Id + "|" + reference.Location).Order(StringComparer.Ordinal).ToArray() };
    }

    internal object CaptureAssetReimportParityState(AssetReimportParityRun? run) => new
    {
        stage = run?.Stage, mode = run?.Mode, history = sessionHistory?.History, producer = run?.Producer?.Status.ToString(),
        requestOwned = sourceReimportRequest != null, activeProducer = assetImportTask?.Status.ToString(),
        cancelled = assetImportProgressCancelled, failed = assetImportProgressFailed, progress = assetImportProgressLogText,
        cloneIsolationVerified = run?.CloneIsolationVerified, completed = run?.Completed, leasesReleased = run?.Released, receipts = run?.Receipts,
        scope = "Real RequestAssetReimports hash-only native texture source preparation/publication services, canonical metadata/Save/history; no synthetic UI menu/progress gesture, typed FBX merge, child-process cancellation or compiled preview claim."
    };

    private static string[] ReimportNativeHashes(Asset asset, string path)
    {
        var clone = AssetCloner.Clone(asset);
        SourceHashesHelper.ConvertPaths(clone, new UDirectory(Path.GetDirectoryName(path)!), UPathType.Absolute);
        return SourceHashesHelper.GetAllHashes(clone).Select(pair => pair.Key + "|" + pair.Value).Order(StringComparer.Ordinal).ToArray();
    }
    private static void ReimportRequire(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private void CheckReimportParityOwners(AssetReimportParityRun run)
    {
        ReimportRequire(ReferenceEquals(workspace, run.Workspace) && ReferenceEquals(sessionHistory, run.History), "Source reimport lost its exact workspace/history.");
        for (var index = 0; index < 2; index++)
            ReimportRequire(ReferenceEquals(authoring!.Find(run.Entries[index].Id), run.Owners[index]) && run.Owners[index].IsPublished
                && ReferenceEquals(run.Owners[index].Document, run.Documents[index]) && run.Documents[index].AuthoredAsset.Id == run.Entries[index].Id,
                "Source reimport replaced an existing native ID/canonical owner.");
        ReimportRequire(run.Workspace.Package.RootAssets.Select(reference => reference.Id + "|" + reference.Location).Order(StringComparer.Ordinal).SequenceEqual(run.RootReferences),
            "Source reimport changed incoming native package root references.");
    }
    private void ChangeReimportPrivateSource(AssetReimportParityRun run, int index, bool restore)
    {
        ReimportRequire(TemplateFileHash(run.SourcePaths[index]) == run.ExpectedSourceSha[index], "Private source changed outside the owned reimport fixture.");
        if (restore) File.WriteAllBytes(run.SourcePaths[index], run.OriginalSourceBytes[index]);
        else
        {
            using var image = Image.Load<Rgba32>(run.SourcePaths[index]);
            image[0, 0] = new Rgba32((byte)(image[0, 0].R + 1), 160, 30, 255); image.SaveAsPng(run.SourcePaths[index]);
        }
        run.ExpectedSourceSha[index] = TemplateFileHash(run.SourcePaths[index]);
    }
    private void CheckReimportHashes(AssetReimportParityRun run, bool updated)
    {
        for (var index = 0; index < 2; index++)
        {
            var sourceAsset = (TextureAsset)run.Documents[index].AuthoredAsset;
            ReimportRequire(SamePath(Path.GetFullPath(sourceAsset.Source.ToOSPath(), Path.GetDirectoryName(run.Entries[index].Path)!), run.SourcePaths[index]),
                "Reimport redirected the actual native source path.");
            var hashes = ReimportNativeHashes(sourceAsset, run.Entries[index].Path);
            if (!updated || run.Mode == 1 && index == 1)
                ReimportRequire(hashes.SequenceEqual(run.OriginalNativeHashes[index]), "Reimport changed an untouched/original asset hash certificate.");
            else
            {
                using var stream = File.OpenRead(run.SourcePaths[index]);
                var current = AssetSourceImport.Fingerprint(stream, CancellationToken.None);
                ReimportRequire(hashes.Any(value => value == new UFile(run.SourcePaths[index]) + "|" + current.Object)
                    && !hashes.SequenceEqual(run.OriginalNativeHashes[index]), "Actual reimport did not update native source ObjectId metadata.");
            }
        }
    }
    private void SaveReimportParityAssets(AssetReimportParityRun run)
    {
        foreach (var doc in run.Documents) doc.Save();
        for (var index = 0; index < 2; index++)
        {
            var item = run.Workspace.FindAsset(run.Entries[index].Id);
            var disk = AssetFileSerializer.Load<Asset>(run.Entries[index].Path,
                assetNamespace: item.Package.Container?.AssetNamespace ?? item.Package.AssetNamespace).Asset;
            ReimportRequire(disk.Id == item.Id && disk.GetType() == typeof(TextureAsset)
                && ReimportNativeHashes(disk, run.Entries[index].Path).SequenceEqual(ReimportNativeHashes(run.Documents[index].AuthoredAsset, run.Entries[index].Path)),
                "Independent native reimport persistence changed asset ID/type/source hashes.");
        }
    }

    private void VerifyReimportNativeBytes(AssetReimportParityRun run, int index, byte[] expected, string phase)
    {
        var actual = File.ReadAllBytes(run.Entries[index].Path);
        var expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected));
        var actualSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual));
        string? expectedPath = null, actualPath = null;
        if (!actual.SequenceEqual(expected))
        {
            var directory = Path.Combine(run.Artifacts, "native-yaml"); Directory.CreateDirectory(directory);
            var label = "mode-" + run.Mode + "-asset-" + index + "-" + phase;
            expectedPath = Path.Combine(directory, label + "-expected.sdtex"); actualPath = Path.Combine(directory, label + "-actual.sdtex");
            File.WriteAllBytes(expectedPath, expected); File.WriteAllBytes(actualPath, actual);
        }
        run.Receipts.Add(new { phase, mode = run.Mode, assetId = run.Entries[index].Id.ToString(), expectedSha, actualSha,
            expectedPath, actualPath, byteEqual = actual.SequenceEqual(expected) });
        ReimportRequire(actual.SequenceEqual(expected), "Reimport native bytes differ at " + phase + "; expectedSHA=" + expectedSha + ", actualSHA=" + actualSha
            + (expectedPath == null ? "" : "; native YAML artifacts: " + expectedPath + " / " + actualPath));
    }

    internal bool StepAssetReimportParityVerification(AssetReimportParityRun run)
    {
        CheckReimportParityOwners(run);
        switch (run.Stage)
        {
            case 0:
                if (!AssetReimportParityReady) return false;
                ChangeReimportPrivateSource(run, 0, false);
                if (run.Mode != 1) ChangeReimportPrivateSource(run, 1, false);
                run.Stage = 1; break;
            case 1:
                if (!AssetReimportParityReady) return false;
                RequestAssetReimports(run.Entries, onlyModified: run.Mode == 1);
                run.Producer = assetImportTask ?? throw new InvalidOperationException("Real source reimport did not retain its producer.");
                ReimportRequire(sourceReimportRequest?.Assets.Length == 2, "Native batch did not retain both canonical source certificates.");
                run.Receipts.Add(new { mode = run.Mode, producerRequested = true, producer = run.Producer.Status.ToString(),
                    sourceSha = run.ExpectedSourceSha.ToArray(), ids = run.Entries.Select(entry => entry.Id.ToString()).ToArray() });
                if (run.Mode == 2)
                {
                    // Cancel the actual retained preparation request before actor publication;
                    // the native task can already be terminal, which is explicitly recorded.
                    CancelAssetImport();
                    ReimportRequire(assetImportCancellation?.IsCancellationRequested == true, "Actual native cancellation token was not signalled.");
                }
                run.Stage = 2; break;
            case 2:
                if (IsAssetImportUpdating || sourceReimportRequest != null) return false;
                ReimportRequire(run.Producer!.IsCompleted, "Native source producer did not reach terminal before releasing its request.");
                if (run.Mode == 2)
                {
                    ReimportRequire(assetImportProgressCancelled && !assetImportProgressFailed && run.History.History.Position == run.Baseline,
                        "Cancelled actual source preparation was published or failed its retirement.");
                    CheckReimportHashes(run, false);
                    for (var index = 0; index < 2; index++) VerifyReimportNativeBytes(run, index, run.OriginalAssetBytes[index], "cancel-baseline");
                    run.Receipts.Add(new { mode = run.Mode, cancelledBeforePublication = true, producerTerminal = run.Producer.Status.ToString(), nativeRequestReleased = true });
                    run.Stage = 8; break;
                }
                ReimportRequire(!assetImportProgressFailed && run.History.History.Position == run.Baseline + 1,
                    "Real source reimport did not publish one successful native batch: " + assetImportProgressLogText);
                var expectedCount = run.Mode == 1 ? 1 : 2;
                var transaction = run.History.History.Entries[run.Baseline]; run.Transaction = transaction.Id;
                ReimportRequire(transaction.Name == $"Update {expectedCount} asset(s) from their source(s)", "Only-modified native result count is incorrect.");
                var participants = run.History.GetParticipants(run.History.Stack.RetrieveAllTransactions().ElementAt(run.Baseline));
                EditorAuthoringState[] expectedOwners = run.Mode == 1 ? [run.Owners[0]] : run.Owners;
                ReimportRequire(participants.Length == expectedCount && participants.ToHashSet().SetEquals(expectedOwners),
                    "Native source batch journal retained different canonical participants.");
                CheckReimportHashes(run, true); SaveReimportParityAssets(run);
                run.UpdatedAssetBytes = run.Entries.Select(entry => File.ReadAllBytes(entry.Path)).ToArray();
                SaveReimportParityAssets(run);
                for (var index = 0; index < 2; index++) VerifyReimportNativeBytes(run, index, run.UpdatedAssetBytes[index], "repeated-save");
                run.Receipts.Add(new { mode = run.Mode, transaction, updatedCount = expectedCount, hashes = run.Documents.Select(doc => ReimportNativeHashes(doc.AuthoredAsset, doc.Path)).ToArray() });
                run.Stage = 3; break;
            case 3:
                if (!AssetReimportParityReady) return false;
                run.History.Undo();
                ReimportRequire(run.History.History.Position == run.Baseline && run.History.History.Entries[run.Baseline].Id == run.Transaction, "Reimport Undo moved a different transaction.");
                CheckReimportHashes(run, false); run.Stage = 4; break;
            case 4:
                if (!AssetReimportParityReady) return false;
                run.History.Redo();
                ReimportRequire(run.History.History.Position == run.Baseline + 1 && run.History.History.Entries[run.Baseline].Id == run.Transaction, "Reimport Redo changed its exact transaction.");
                CheckReimportHashes(run, true); SaveReimportParityAssets(run);
                for (var index = 0; index < 2; index++) VerifyReimportNativeBytes(run, index, run.UpdatedAssetBytes![index], "redo-save");
                run.Stage = 5; break;
            case 5:
                if (!AssetReimportParityReady) return false;
                run.History.Undo(); CheckReimportHashes(run, false); SaveReimportParityAssets(run);
                for (var index = 0; index < 2; index++) VerifyReimportNativeBytes(run, index, run.OriginalAssetBytes[index], "final-undo-save");
                run.Stage = 8; break;
            case 8:
                if (!AssetReimportParityReady) return false;
                for (var index = 0; index < 2; index++) ChangeReimportPrivateSource(run, index, true);
                CheckReimportHashes(run, false);
                ReimportRequire(run.History.History.Position == run.Baseline && run.Documents.All(doc => !doc.IsDirty), "Reimport did not restore clean original native owners/history.");
                if (++run.Mode < 3) { run.Stage = 0; break; }
                run.Completed = true; return true;
        }
        return false;
    }
    internal void RetireAssetReimportParityVerification(AssetReimportParityRun run)
    {
        if (run.Released) return;
        if (ReferenceEquals(assetImportTask, run.Producer)) CancelAssetImport();
        // Production request retains its own view leases until its actual terminal
        // pump/outer retirement. Release only this diagnostic's independent views.
        var failures = new List<Exception>();
        foreach (var lease in run.Leases)
            try { lease.Dispose(); } catch (Exception error) { failures.Add(error); }
        run.Released = failures.Count == 0;
        if (failures.Count != 0) throw new AggregateException("Native reimport diagnostic view retirement failed.", failures);
    }
}
