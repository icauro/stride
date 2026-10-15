using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private object? pollingCertificateParityEvidence;
    internal object? PollingCertificateParityVerificationEvidence => pollingCertificateParityEvidence;
    internal void VerifyNativePollingCertificateCacheParity()
    {
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!args.Contains("--ui-test") || workspace == null || authoring == null
            || !Path.GetFullPath(workspace.PackagePath).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Polling certificate verification requires the loaded disposable UI project.");
        var byteEquivalence = PollingCertificateByteVerification.Verify();
        var pathEquivalence = PollingAssetPathVerification.Verify();
        var fontPool = FontSourcePoolParityVerification.Verify();
        var thumbnailArtifacts = NativeThumbnailArtifactParityVerification.Verify(Path.Combine(privateRoot,
            "ThumbnailArtifactParity-" + Guid.NewGuid().ToString("N")));
        PrepareSceneCompositorReload(out var scope);
        if (scope?.CompositorOwner.Document is not AssetMetadataDocument owner || !owner.CanSave || owner.IsDirty)
            throw new InvalidOperationException("An unchanged editable canonical compositor is required.");
        var entry = workspace.Assets.Single(item => item.Id == scope.Compositor);
        var nativeItem = workspace.FindAsset(entry.Id);
        var file = nativeItem.FullPath.ToOSPath();
        if (!Path.GetFullPath(file).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The compiler root file must belong to the disposable fixture.");
        var originalBytes = File.ReadAllBytes(file);
        var originalModified = File.GetLastWriteTimeUtc(file);
        var originalTags = owner.AuthoredAsset.Tags.ToArray();
        var originalRevision = owner.EditRevision;
        var originalState = scope.CompositorOwner;
        using var originalLease = originalState.AcquireView();
        var cache = new AuthoredImportPollingCache();
        var captures = 0;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        AuthoredImportPollingCache.Entry Capture()
            => cache.Capture(workspace, authoring, entry, AuthoredImportPollingCache.CaptureInputs(workspace, authoring), _ =>
            { captures++; return CaptureCompositorCompilation(entry, false); });
        var edited = false;
        try
        {
            var cold = Capture();
            var warm = Capture();
            Require(ReferenceEquals(cold, warm) && ReferenceEquals(cold.Snapshot, warm.Snapshot) && captures == 1,
                "Unchanged real project inputs must reuse the exact captured compiler snapshot.");
            Require(cold.Inputs.Length == 64, "The derived polling certificate must remain compact, independently of session inventory size.");
            var tag = "Polling certificate parity " + Guid.NewGuid().ToString("N");
            owner.SetTags([.. originalTags, tag]); edited = true;
            var changed = Capture();
            Require(owner.EditRevision != originalRevision && !ReferenceEquals(changed.Snapshot, cold.Snapshot)
                && changed.Snapshot.Revision != cold.Snapshot.Revision && captures == 2,
                "An actual canonical tag edit must invalidate and change the compiler snapshot.");
            owner.Undo(); edited = false;
            var undone = Capture();
            Require(owner.EditRevision == originalRevision && owner.AuthoredAsset.Tags.SequenceEqual(originalTags)
                && !ReferenceEquals(undone.Snapshot, changed.Snapshot) && undone.Snapshot.Revision == cold.Snapshot.Revision,
                "Real history undo must restore compiler content while invalidating the edited snapshot.");
            Require(ReferenceEquals(Capture(), undone), "Restored unchanged inputs must warm-reuse their new snapshot.");
            File.SetLastWriteTimeUtc(file, originalModified.AddSeconds(2));
            Require(File.GetLastWriteTimeUtc(file) != originalModified, "The private filesystem must expose the actual timestamp change.");
            var changedFile = Capture();
            Require(!ReferenceEquals(changedFile.Snapshot, undone.Snapshot), "A real asset file-stat change must invalidate the warm polling entry.");
            File.SetLastWriteTimeUtc(file, originalModified);
            var restoredFile = Capture();
            Require(!ReferenceEquals(restoredFile.Snapshot, changedFile.Snapshot)
                && restoredFile.Snapshot.Revision == cold.Snapshot.Revision,
                "Restoring file provenance must recapture the actual unchanged compiler content.");
            // Exercise actual publication lifetime while retaining the original
            // owner. Do not invent a generation or register a duplicate graph.
            authoring.Unpublish(originalState);
            var unpublished = Capture();
            Require(!ReferenceEquals(unpublished.Snapshot, restoredFile.Snapshot),
                "Losing the actual canonical publication must invalidate its old polling entry.");
            authoring.Publish(originalState);
            var republished = Capture();
            Require(!ReferenceEquals(republished.Snapshot, unpublished.Snapshot)
                && ReferenceEquals(authoring.Find(entry.Id), originalState)
                && republished.Snapshot.Revision == cold.Snapshot.Revision,
                "Republishing the retained native owner must invalidate the fallback snapshot and restore canonical content.");
            Require(ReferenceEquals(Capture(), republished), "An unchanged restored publication must warm-reuse its exact snapshot.");
            Require(!owner.IsDirty && File.ReadAllBytes(file).SequenceEqual(originalBytes)
                && File.GetLastWriteTimeUtc(file) == originalModified,
                "Verification must preserve original asset bytes, timestamp and clean authored state.");
            pollingCertificateParityEvidence = new { assetId = entry.Id.ToString(), ownerGeneration = originalState.Generation,
                captures, byteEquivalence, pathEquivalence, fontPool, thumbnailArtifacts,
                warmSnapshotIdentityPreserved = true, nativeEditAndUndoInvalidated = true,
                filesystemTimestampInvalidated = true, canonicalPublicationInvalidated = true,
                generationReplacementCovered = false, assetBytesPreserved = true,
                boundary = "Actual loaded private workspace, native canonical graph/history, compiler source snapshots and filesystem provenance; no compiler execution, UI-input path, external file replacement or generation-swap coverage is inferred." };
        }
        finally
        {
            if (edited) owner.Undo();
            if (!originalState.IsPublished) authoring.Publish(originalState);
            File.SetLastWriteTimeUtc(file, originalModified);
        }
    }
}
