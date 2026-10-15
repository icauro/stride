using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Stride.Editor.Thumbnails;

namespace Stride.Editor.ImGui;

internal static class NativeThumbnailArtifactParityVerification
{
    internal static object Verify(string ownedDirectory)
    {
        var root = Path.GetFullPath(ownedDirectory);
        Directory.CreateDirectory(root);
        var modulePath = Path.Combine(root, "ActualEditorModule.dll");
        if (File.Exists(modulePath)) throw new IOException("Thumbnail artifact parity requires a fresh owned directory.");
        var canonical = typeof(NativeThumbnailArtifactParityVerification).Assembly;
        File.Copy(canonical.Location, modulePath, false);
        var sourceHash = Hash(modulePath);
        var context = new AssemblyLoadContext("Owned thumbnail artifact parity " + Guid.NewGuid().ToString("N"), isCollectible: true);
        NativeThumbnailAssemblySnapshot? source = null, recaptured = null;
        IDisposable? job = null, queued = null;
        try
        {
            // This is a genuine loaded PE/module in an isolated collectible
            // context, so unrelated live editor thumbnail owners cannot alter
            // the test's last-lease boundary.
            var actual = context.LoadFromAssemblyPath(modulePath);
            source = NativeThumbnailAssemblyCertificates.Capture(actual);
            var certificate = source.Certificate; var artifact = source.Artifact;
            if (certificate.Sha256 != sourceHash || certificate.Identity != actual.FullName
                || certificate.ModuleVersionId != actual.ManifestModule.ModuleVersionId.ToString("D")
                || artifact.Length != new FileInfo(modulePath).Length || !artifact.IsAvailable)
                throw new InvalidDataException("Captured artifact must certify the actual loaded PE identity, MVID and streamed SHA256.");
            artifact.Validate(certificate);
            var artifactPath = artifact.InspectionPath;
            var immutable = false;
            try { using var attemptedWrite = new FileStream(artifactPath, FileMode.Open, FileAccess.Write, FileShare.None); }
            catch (IOException) { immutable = true; }
            catch (UnauthorizedAccessException) { immutable = true; }
            if (!immutable) throw new InvalidOperationException("The live private artifact did not reject write access.");
            var badHash = new NativeThumbnailAssemblyInput { Path = certificate.Path, Identity = certificate.Identity,
                ModuleVersionId = certificate.ModuleVersionId, Sha256 = new string('0', 64) };
            var rejected = false;
            try { artifact.Validate(badHash); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("A changed expected hash must be rejected before deployment.");

            job = artifact.AcquireLease(); queued = artifact.AcquireLease();
            source.Dispose(); source = null;
            queued.Dispose(); queued = null;
            if (!artifact.IsAvailable || !File.Exists(artifactPath))
                throw new InvalidOperationException("Source and extra lease release prematurely retired the active job artifact.");
            var deployed = Path.Combine(root, "StreamedDeployment.dll");
            artifact.CopyTo(deployed, certificate);
            using (var stream = File.OpenRead(deployed))
            using (var pe = new PEReader(stream))
            {
                var metadata = pe.GetMetadataReader();
                if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D") != certificate.ModuleVersionId)
                    throw new InvalidDataException("Streamed deployment changed the actual module identity.");
            }
            if (Hash(deployed) != certificate.Sha256 || new FileInfo(deployed).Length != artifact.Length
                || AssemblyName.GetAssemblyName(deployed).FullName != certificate.Identity)
                throw new InvalidDataException("Independent streamed deployment verification failed.");
            job.Dispose(); job = null;
            if (artifact.IsAvailable || File.Exists(artifactPath))
                throw new InvalidOperationException("Last artifact lease did not release and delete its private file.");
            var expiredRejected = false;
            try { using var expired = artifact.AcquireLease(); }
            catch (ObjectDisposedException) { expiredRejected = true; }
            catch (InvalidOperationException) { expiredRejected = true; }
            if (!expiredRejected) throw new InvalidOperationException("Expired artifact must refuse new leases.");

            recaptured = NativeThumbnailAssemblyCertificates.Capture(actual);
            if (ReferenceEquals(recaptured.Artifact, artifact) || !recaptured.Artifact.IsAvailable
                || recaptured.Certificate.Sha256 != sourceHash)
                throw new InvalidOperationException("An expired weak descriptor must recapture the same actual loaded module.");
            var recapturedPath = recaptured.Artifact.InspectionPath;
            var cleanupBefore = NativeThumbnailAssemblyArtifact.PendingCleanupCount;
            var failedFinalLease = recaptured;
            var deletionBlocked = false;
            // The actual OS handle denies deletion of this private artifact.
            // Its release must allow the production retry owner to finish.
            using (var blocker = new FileStream(recapturedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { failedFinalLease.Dispose(); }
                catch (IOException) { deletionBlocked = true; }
                if (!deletionBlocked || NativeThumbnailAssemblyArtifact.PendingCleanupCount != cleanupBefore + 1
                    || failedFinalLease.Artifact.IsAvailable || !File.Exists(recapturedPath))
                    throw new InvalidOperationException("A real deletion block must retain exactly one failed cleanup owner.");
            }
            NativeThumbnailAssemblyArtifact.PumpPendingCleanup(requireComplete: true);
            if (File.Exists(recapturedPath) || NativeThumbnailAssemblyArtifact.PendingCleanupCount != cleanupBefore)
                throw new InvalidOperationException("Actual cleanup retry did not release the blocked private artifact.");
            failedFinalLease.Dispose(); recaptured = null;
            if (NativeThumbnailAssemblyArtifact.PendingCleanupCount != cleanupBefore)
                throw new InvalidOperationException("Late final-lease disposal must be idempotent after cleanup retry.");
            VerifyAggregateCleanup(actual, modulePath);
            if (Hash(modulePath) != sourceHash || Hash(canonical.Location) != sourceHash)
                throw new InvalidDataException("Artifact parity changed an actual source module.");
            return new { actualModuleIdentity = actual.FullName, sha256 = sourceHash,
                actualMvid = actual.ManifestModule.ModuleVersionId, immutableArtifact = true, changedHashRejected = true,
                sourceReleasedJobLeaseUsable = true, independentlyVerifiedStreamedCopy = true,
                lastLeaseDeletedFile = true, expiredDescriptorRecaptured = true,
                realOsDeletionBlockRetried = true, failedCleanupOwnerReleased = true, lateDisposeIdempotent = true,
                aggregateAttemptsEveryActualArtifact = true,
                gaps = new[] { "Real canceled queue jobs and collectible-context GC/unload are not asserted by this lease case." } };
        }
        finally
        {
            queued?.Dispose(); job?.Dispose(); source?.Dispose(); recaptured?.Dispose(); context.Unload();
        }
    }
    private static void VerifyAggregateCleanup(Assembly actual, string privateModulePath)
    {
        var independent = new AssemblyLoadContext("Owned aggregate artifact parity " + Guid.NewGuid().ToString("N"), isCollectible: true);
        NativeThumbnailAssemblySnapshot? first = null, second = null;
        NativeThumbnailArtifactLeaseSet? set = null;
        try
        {
            first = NativeThumbnailAssemblyCertificates.Capture(actual);
            second = NativeThumbnailAssemblyCertificates.Capture(independent.LoadFromAssemblyPath(privateModulePath));
            if (ReferenceEquals(first.Artifact, second.Artifact))
                throw new InvalidOperationException("Aggregate proof requires two independent real loaded-module artifacts.");
            var firstPath = first.Artifact.InspectionPath; var secondPath = second.Artifact.InspectionPath;
            set = new NativeThumbnailArtifactLeaseSet(new[] { first, second });
            first.Dispose(); second.Dispose();
            var pendingBefore = NativeThumbnailAssemblyArtifact.PendingCleanupCount;
            var rejected = false;
            // The set disposes in reverse order: block the first attempted
            // final lease and require that the later unblocked one still runs.
            using (var blocker = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { set.Dispose(); }
                catch (AggregateException error) when (error.InnerExceptions.Any(failure => failure is IOException)) { rejected = true; }
                if (!rejected || File.Exists(firstPath) || first.Artifact.IsAvailable
                    || !File.Exists(secondPath) || second.Artifact.IsAvailable
                    || NativeThumbnailAssemblyArtifact.PendingCleanupCount != pendingBefore + 1)
                    throw new InvalidOperationException("Aggregate cleanup must release the unblocked artifact despite an earlier real deletion failure.");
            }
            NativeThumbnailAssemblyArtifact.PumpPendingCleanup(requireComplete: true);
            if (File.Exists(secondPath) || NativeThumbnailAssemblyArtifact.PendingCleanupCount != pendingBefore)
                throw new InvalidOperationException("Aggregate failed-owner retry did not finish actual private-file cleanup.");
            set.Dispose(); set.Dispose();
            if (NativeThumbnailAssemblyArtifact.PendingCleanupCount != pendingBefore)
                throw new InvalidOperationException("Aggregate late disposal was not idempotent.");
        }
        finally { set?.Dispose(); first?.Dispose(); second?.Dispose(); independent.Unload(); }
    }
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
