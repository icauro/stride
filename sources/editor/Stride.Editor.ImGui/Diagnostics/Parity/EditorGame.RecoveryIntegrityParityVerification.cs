using System.Security.Cryptography;
using System.Text.Json;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private RecoveryIntegrityParityRun? recoveryIntegrityParityOwner;
    internal sealed class RecoveryIntegrityParityRun
    {
        internal required EditorRecoveryStore Store;
        internal required Task Writer;
        internal required SceneDocument Scene;
        internal required EditorSessionHistory History;
        internal required EditorAuthoringState State;
        internal required EditorRecoveryRecord Record;
        internal required string Root;
        internal required byte[] Disk;
        internal required int HistoryPosition;
        internal required Guid[] HistoryIds;
        internal object? Evidence;
        internal int Stage;
    }

    internal RecoveryIntegrityParityRun BeginRecoveryIntegrityParityVerification(string ownedDirectory)
    {
        var artifacts = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
        var root = Path.GetFullPath(ownedDirectory);
        if (!args.Contains("--ui-test") || workspace == null || sessionHistory == null || document is not SceneDocument scene
            || scene.IsDirty || IsAuthoringPipelineUpdating || recoveryIntegrityParityOwner != null
            || string.IsNullOrEmpty(artifacts) || !root.StartsWith(Path.GetFullPath(artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(root))
            throw new InvalidOperationException("Recovery integrity verification requires a fresh private artifact journal and settled clean native scene.");
        var history = sessionHistory;
        var state = history.Registry.Find(scene.Path) ?? throw new InvalidOperationException("The real scene has no canonical owner.");
        var disk = File.ReadAllBytes(scene.Path);
        var baselinePosition = history.History.Position;
        var baselineIds = history.History.Entries.Take(baselinePosition).Select(entry => entry.Id).ToArray();
        var tag = "Recovery integrity " + Guid.NewGuid().ToString("N");
        EditorRecoveryRecord record;
        scene.SetTags([..scene.Asset.Tags, tag]);
        try
        {
            if (!scene.IsDirty || history.History.Position != baselinePosition + 1)
                throw new InvalidOperationException("Recovery arrangement requires one actual native authored transaction.");
            record = EditorRecoveryDocuments.Capture(scene, workspace.Session.FindAsset(scene.Asset.Id));
            var log = new LoggerResult();
            var independent = EditorRecoveryDocuments.LoadRecoveryAsset(record, log).Asset;
            if (independent is not SceneAsset || log.HasErrors || independent.Id != scene.Asset.Id || !independent.Tags.Contains(tag))
                throw new InvalidDataException("Actual captured native recovery payload must independently deserialize before corruption.");
        }
        finally { scene.Undo(); }
        if (scene.IsDirty || history.History.Position != baselinePosition || !File.ReadAllBytes(scene.Path).SequenceEqual(disk)
            || !history.History.Entries.Take(baselineIds.Length).Select(entry => entry.Id).SequenceEqual(baselineIds))
            throw new InvalidOperationException("Recovery arrangement must restore original clean native owner and disk before asynchronous journal work.");
        var store = new EditorRecoveryStore(workspace.OpenPath, root);
        var run = new RecoveryIntegrityParityRun { Store = store, Scene = scene, History = history, State = state,
            Record = record, Root = root, Disk = disk, HistoryPosition = baselinePosition,
            HistoryIds = history.History.Entries.Select(entry => entry.Id).ToArray(), Writer = store.CheckpointAsync([record], []) };
        recoveryIntegrityParityOwner = run;
        return run;
    }

    internal bool StepRecoveryIntegrityParityVerification(RecoveryIntegrityParityRun run)
    {
        if (!run.Writer.IsCompleted) return false;
        run.Writer.GetAwaiter().GetResult();
        AssertRecoveryIntegrityOwner(run);
        if (run.Stage == 0)
        {
            var manifestPath = Directory.GetFiles(run.Root, "session.json", SearchOption.AllDirectories).Single();
            var manifestBytes = File.ReadAllBytes(manifestPath);
            var manifest = JsonSerializer.Deserialize<EditorRecoveryManifest>(manifestBytes)
                ?? throw new InvalidDataException("Actual native recovery checkpoint manifest is unavailable.");
            var entry = manifest.Documents.Single();
            var candidate = new EditorRecoveryCandidate(manifestPath, manifest);
            var valid = run.Store.Read(candidate, entry);
            if (!valid.Payload.SequenceEqual(run.Record.Payload) || entry.AssetId != run.Scene.Asset.Id.ToString())
                throw new InvalidDataException("Durable native journal must reproduce the exact canonical payload and asset identity.");
            var payloadPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, entry.PayloadHash + ".snapshot");
            var payload = File.ReadAllBytes(payloadPath);
            if (payload.Length == 0) throw new InvalidDataException("Native payload cannot be empty.");
            payload[payload.Length / 2] ^= 0x01;
            File.WriteAllBytes(payloadPath, payload);
            Exception? rejection = null;
            try { run.Store.Read(candidate, entry); }
            catch (InvalidDataException error) { rejection = error; }
            if (rejection == null || !rejection.Message.Contains("integrity check", StringComparison.OrdinalIgnoreCase)
                || !File.ReadAllBytes(manifestPath).SequenceEqual(manifestBytes)
                || !File.ReadAllBytes(payloadPath).SequenceEqual(payload))
                throw new InvalidOperationException("Actual production journal Read must reject corrupted bytes without repairing the payload or altering manifest identity.");
            AssertRecoveryIntegrityOwner(run);
            var independentDisk = AssetFileSerializer.Load<SceneAsset>(run.Scene.Path).Asset;
            if (independentDisk.Id != run.Scene.Asset.Id || !independentDisk.Tags.SequenceEqual(run.Scene.Asset.Tags))
                throw new InvalidOperationException("Independent scene reload must retain the original owner data after corrupt journal rejection.");
            run.Evidence = new { assetId = run.Scene.Asset.Id.ToString(), manifestPath, payloadPath,
                originalPayloadSha256 = entry.PayloadHash, corruptPayloadSha256 = Convert.ToHexString(SHA256.HashData(payload)),
                actualNativePayloadRoundTrip = true, actualProductionIntegrityRejection = rejection.ToString(),
                canonicalOwnerHistoryAndDiskUnchanged = true, independentNativeSceneReload = true,
                scope = "Real canonical native payload capture, durable production journal and hash rejection before any restore publication; service/native integrity scope, not crashed-process discovery, receiver modal or complete H16 recovery acceptance." };
            run.Stage = 1;
            run.Writer = run.Store.CompleteAsync();
            return false;
        }
        recoveryIntegrityParityOwner = null;
        return true;
    }

    private void AssertRecoveryIntegrityOwner(RecoveryIntegrityParityRun run)
    {
        if (!ReferenceEquals(sessionHistory, run.History) || !ReferenceEquals(run.History.Registry.Find(run.Scene.Path), run.State)
            || !run.State.IsPublished || !ReferenceEquals(run.State.Document, run.Scene) || run.Scene.IsDirty
            || run.History.History.Position != run.HistoryPosition
            || !run.History.History.Entries.Select(entry => entry.Id).SequenceEqual(run.HistoryIds)
            || !File.ReadAllBytes(run.Scene.Path).SequenceEqual(run.Disk))
            throw new InvalidOperationException("Corrupt journal verification changed the actual canonical scene, history or original disk bytes.");
    }

    internal bool TryDrainRecoveryIntegrityParityVerification()
    {
        var run = recoveryIntegrityParityOwner;
        if (run == null) return true;
        if (!run.Writer.IsCompleted) return false;
        // Observe writer failure without confusing it with cleanup: no document
        // or compiler crossed into the writer and its owning gate has completed.
        _ = run.Writer.Exception;
        AssertRecoveryIntegrityOwner(run);
        recoveryIntegrityParityOwner = null;
        return true;
    }
}
