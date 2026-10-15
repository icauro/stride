using Stride.Assets;
using Stride.Assets.Models;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private MixedSaveParityRun? mixedSaveParityOwner;
    internal sealed class MixedSaveParityRun
    {
        internal required EditorSessionHistory History;
        internal required IAssetTagDocument[] Owners;
        internal required byte[][] Baselines;
        internal required long Position;
        internal required Guid[] TransactionIds;
        internal required string Tag;
        internal string? ConflictPath, ConflictError;
        internal string[] AcknowledgedPaths = [];
        internal Exception? Failure;
        internal int Stage;
        internal bool Saved;
        internal Guid[] OwnedTransactions = [];
        internal bool CleanupStarted;
        internal object? Evidence;
        internal string[] SourceSha256 = [], CanonicalSha256 = [];
    }

    internal MixedSaveParityRun BeginMixedSaveParityVerification()
    {
        if (!args.Contains("--ui-test") || workspace == null || sessionHistory == null || document == null
            || IsAuthoringPipelineUpdating || documents.HasDirtyDocuments || workspace.HasPackageChanges)
            throw new InvalidOperationException("Mixed Save All requires a clean, loaded private project and settled authoring pipeline.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(document.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mixed Save All may alter only the actual isolated UI fixture.");
        var sphere = workspace.Assets.Single(asset => asset.Name == "Sphere" && asset.AssetType == typeof(ProceduralModelAsset));
        var settings = workspace.Assets.Single(asset => asset.AssetType == typeof(GameSettingsAsset));
        IAssetTagDocument[] owners = [document, GetAssetTagDocument(sphere, false, false), GetAssetTagDocument(settings, false, false)];
        if (owners.Distinct().Count() != 3 || owners.Any(owner => owner.IsDirty || !owner.CanSave
            || !Path.GetFullPath(owner.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Three distinct editable clean native owners are required.");
        var normalizationPosition = sessionHistory.History.Position;
        var normalizationIds = sessionHistory.History.Entries.Select(entry => entry.Id).ToArray();
        var sourceHashes = owners.Select(owner => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(owner.Path)))).ToArray();
        // Materialize the actual native serializer's canonical defaults before
        // arranging mutations; no expected-byte rewrite or history edit.
        foreach (var owner in owners) owner.Save();
        if (owners.Any(owner => owner.IsDirty) || sessionHistory.History.Position != normalizationPosition
            || !sessionHistory.History.Entries.Select(entry => entry.Id).SequenceEqual(normalizationIds))
            throw new InvalidOperationException("Native baseline normalization must leave all owners clean and native history unchanged.");
        var run = new MixedSaveParityRun { History = sessionHistory, Owners = owners,
            Baselines = owners.Select(owner => File.ReadAllBytes(owner.Path)).ToArray(), Position = sessionHistory.History.Position,
            TransactionIds = sessionHistory.History.Entries.Select(entry => entry.Id).ToArray(), Tag = "Mixed Save proof " + Guid.NewGuid().ToString("N") };
        run.SourceSha256 = sourceHashes;
        run.CanonicalSha256 = run.Baselines.Select(bytes => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))).ToArray();
        if (mixedSaveParityOwner != null) throw new InvalidOperationException("The prior mixed save verification still owns cleanup.");
        mixedSaveParityOwner = run;
        foreach (var owner in owners)
        {
            owner.SetTags([..owner.AuthoredAsset.Tags, run.Tag]);
            run.OwnedTransactions = sessionHistory.History.Entries.Skip(run.TransactionIds.Length).Select(entry => entry.Id).ToArray();
        }
        if (owners.Any(owner => !owner.IsDirty) || sessionHistory.History.Position != run.Position + 3)
            throw new InvalidOperationException("Actual canonical tag edits must produce exactly three native dirty-owner transactions.");
        var pending = sessionHistory.Registry.PublishedStates.Where(state => state.Document is not PackageMetadataDocument && state.Document.IsDirty).ToArray();
        if (pending.Length != 3 || pending.Any(state => !owners.Contains(state.Document as IAssetTagDocument)))
            throw new InvalidOperationException("Mixed Save All fixture must own every pending document save.");
        var failed = (IAssetTagDocument)pending[^1].Document;
        run.ConflictPath = failed.Path;
        var failedIndex = Array.IndexOf(owners, failed);
        var conflicting = run.Baselines[failedIndex].Concat(System.Text.Encoding.UTF8.GetBytes("\n# controlled external native-save conflict\n")).ToArray();
        File.WriteAllBytes(failed.Path, conflicting);
        try
        {
            Exception? rejection = null;
            try { SaveAllDocuments(); } catch (IOException error) { rejection = error; }
            if (rejection == null || !rejection.Message.Contains("changed on disk", StringComparison.OrdinalIgnoreCase)
                || !failed.IsDirty || !File.ReadAllBytes(failed.Path).SequenceEqual(conflicting))
                throw new InvalidOperationException("Real Save All must reject the changed file without acknowledging or overwriting its dirty owner.");
            run.ConflictError = rejection.ToString();
            var preceding = pending[..^1].Select(state => (IAssetTagDocument)state.Document).ToArray();
            foreach (var owner in preceding)
                if (owner.IsDirty || !AssetFileSerializer.Load<Asset>(owner.Path).Asset.Tags.Contains(run.Tag))
                    throw new InvalidOperationException("Each earlier successful native file must independently persist and acknowledge its actual owner.");
            run.AcknowledgedPaths = preceding.Select(owner => owner.Path).ToArray();
            if (sessionHistory.History.Position != run.Position + 3 || owners.Any(owner => !ReferenceEquals(sessionHistory.Registry.Find(owner.Path)?.Document, owner)))
                throw new InvalidOperationException("Partial Save All must preserve canonical owner identities and all authored history.");
        }
        finally
        {
            // Resolve only the test-authored external conflict; no disk guard is
            // bypassed and no authored revision is reloaded or discarded.
            if (!File.ReadAllBytes(failed.Path).SequenceEqual(conflicting))
                throw new IOException("Controlled conflict changed unexpectedly; refusing to overwrite newer bytes.");
            File.WriteAllBytes(failed.Path, run.Baselines[failedIndex]);
        }
        SaveAllDocuments(() => run.Saved = true, error => run.Failure = error);
        return run;
    }

    internal bool StepMixedSaveParityVerification(MixedSaveParityRun run)
    {
        if (run.Failure != null) throw new InvalidOperationException("Actual mixed Save All failed.", run.Failure);
        if (!run.Saved || IsPackageMetadataSaving) return false;
        if (!ReferenceEquals(sessionHistory, run.History)) throw new InvalidOperationException("Mixed save history owner changed.");
        if (run.Stage == 0)
        {
            foreach (var owner in run.Owners)
                if (owner.IsDirty || !ReferenceEquals(run.History.Registry.Find(owner.Path)?.Document, owner)
                    || !AssetFileSerializer.Load<Asset>(owner.Path).Asset.Tags.Contains(run.Tag))
                    throw new InvalidOperationException("Retry must independently persist and acknowledge every original canonical owner.");
            if (run.History.History.Position != run.Position + 3)
                throw new InvalidOperationException("Retry must not fabricate or discard native authoring history.");
            run.Evidence = new { successfulEarlierFiles = run.AcknowledgedPaths, conflictPath = run.ConflictPath,
                actualConflict = run.ConflictError, nativePerFileAcknowledgments = true, retryPreservesOwnersAndHistory = true,
                independentSceneProceduralGameSettingsReload = true,
                sourceSha256 = run.SourceSha256, canonicalBaselineSha256 = run.CanonicalSha256,
                scope = "Actual production SaveAll native service path, three canonical scene/procedural/GameSettings tag edits; fixture arrangement is not UI input. Earlier successful files remain acknowledged: no whole-save atomicity claim." };
            for (var index = 0; index < 3; index++) run.History.Undo();
            if (run.History.History.Position != run.Position
                || !run.History.History.Entries.Take(run.TransactionIds.Length).Select(entry => entry.Id).SequenceEqual(run.TransactionIds))
                throw new InvalidOperationException("Cleanup must undo only the three exact fixture transactions and retain the original history prefix.");
            run.Stage = 1; run.Saved = false;
            SaveAllDocuments(() => run.Saved = true, error => run.Failure = error);
            return false;
        }
        for (var index = 0; index < run.Owners.Length; index++)
            if (run.Owners[index].IsDirty || !File.ReadAllBytes(run.Owners[index].Path).SequenceEqual(run.Baselines[index]))
                throw new InvalidOperationException("Mixed-save cleanup must restore exact original native asset bytes and clean owners.");
        mixedSaveParityOwner = null;
        return true;
    }

    internal bool TryDrainMixedSaveParityVerification()
    {
        var run = mixedSaveParityOwner;
        if (run == null) return true;
        if (IsPackageMetadataSaving) return false;
        if (!ReferenceEquals(sessionHistory, run.History)) throw new InvalidOperationException("Cannot compensate a replaced mixed-save history owner.");
        if (!run.CleanupStarted)
        {
            var entries = run.History.History.Entries;
            if (!entries.Take(run.TransactionIds.Length).Select(entry => entry.Id).SequenceEqual(run.TransactionIds)
                || !entries.Skip(run.TransactionIds.Length).Select(entry => entry.Id).SequenceEqual(run.OwnedTransactions)
                || run.History.History.Position < run.Position || run.History.History.Position > run.Position + run.OwnedTransactions.Length)
                throw new InvalidOperationException("Mixed-save compensation refuses history changed by another owner.");
            while (run.History.History.Position > run.Position) run.History.Undo();
            var errors = new List<Exception>();
            foreach (var owner in run.Owners)
                try { owner.Save(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("All mixed-save native cleanup owners were attempted; failed owners remain retained.", errors);
            run.Failure = null; run.Saved = false; run.CleanupStarted = true;
            SaveAllDocuments(() => run.Saved = true, error => run.Failure = error);
            return false;
        }
        if (run.Failure != null) throw new InvalidOperationException("Mixed-save native compensation failed; its exact owner remains retained.", run.Failure);
        if (!run.Saved) return false;
        for (var index = 0; index < run.Owners.Length; index++)
            if (run.Owners[index].IsDirty || !File.ReadAllBytes(run.Owners[index].Path).SequenceEqual(run.Baselines[index]))
                throw new InvalidOperationException("Mixed-save compensation did not restore original native files and clean owners.");
        mixedSaveParityOwner = null;
        return true;
    }
}
