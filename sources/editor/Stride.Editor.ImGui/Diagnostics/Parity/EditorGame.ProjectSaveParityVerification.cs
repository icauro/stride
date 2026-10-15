using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal sealed class ProjectSaveParityRun
    {
        internal required ProjectWorkspace Workspace;
        internal required Package Package;
        internal required PackageMetadataDocument Metadata;
        internal required byte[] OriginalBytes;
        internal byte[] LatestSavedBytes = [];
        internal long Revision;
        internal int Stage, Continued, Redirected;
        internal Exception? Failure;
        internal string? ProgressId;
        internal IDisposable? MetadataLease;
        internal long CapturedMetadataRevision;
        internal byte[] CleanSaveBytes = [];
        internal Guid[] CleanSaveHistory = [];
        internal long CleanSaveHistoryPosition, CleanSaveRevision;
    }

    internal bool IsProjectSaveParityVerificationReady => workspace?.Package != null && SessionAuthoringHistory != null
        && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !documents.HasDirtyDocuments && afterDiscard == null;

    internal ProjectSaveParityRun BeginProjectSaveParityVerification()
    {
        if (!IsProjectSaveParityVerificationReady || workspace?.Package is not { } package || SessionAuthoringHistory is not { } history)
            throw new InvalidOperationException($"Save parity verification is not ready: workspace={workspace != null}, history={SessionAuthoringHistory != null}, pipeline={IsAuthoringPipelineUpdating}, build={IsUserProjectBuilding}, dirtyDocuments={documents.HasDirtyDocuments}, dirtyPackages={workspace?.HasPackageChanges}, replacement={afterDiscard != null}.");
        var owner = history.Registry.GetOrCreate(package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(workspace, package, history));
        var run = new ProjectSaveParityRun { Workspace = workspace, Package = package, Metadata = owner.Document,
            OriginalBytes = File.ReadAllBytes(package.FullPath.ToOSPath()), Revision = owner.Document.Revision };
        run.MetadataLease = owner.AcquireView();
        if (workspace.HasPackageChanges)
        {
            run.Stage = -1;
            RequestPackageMetadataSave(() => run.Stage = -2, error => run.Failure = error);
            return run;
        }
        StartProjectSaveParityReplacementCases(run);
        return run;
    }

    private void StartProjectSaveParityReplacementCases(ProjectSaveParityRun run)
    {
        var package = run.Package;
        run.OriginalBytes = File.ReadAllBytes(package.FullPath.ToOSPath());
        run.Revision = run.Metadata.Revision;
        run.Stage = 0;
        // Exercise actual Cancel state without saving or replacing any owner.
        package.IsDirty = true;
        RequestReplace(() => run.Continued++, true);
        if (afterDiscard == null) throw new InvalidOperationException("Dirty native metadata must require a replacement decision.");
        ClearDocumentReplacementState();
        if (!package.IsDirty || run.Continued != 0 || !ReferenceEquals(workspace, run.Workspace)
            || !File.ReadAllBytes(package.FullPath.ToOSPath()).SequenceEqual(run.OriginalBytes))
            throw new InvalidOperationException("Cancel must preserve dirty ownership and original disk bytes.");
        RequestReplace(() => run.Continued++, true);
        SaveRequestedReplacement();
        if (!IsPackageMetadataSaving || run.Continued != 0)
            throw new InvalidOperationException("Replacement must wait for actual asynchronous metadata publication.");
    }

    internal object? StepProjectSaveParityVerification(ProjectSaveParityRun run)
    {
        if (!ReferenceEquals(workspace, run.Workspace)) throw new InvalidOperationException("The save fixture workspace changed.");
        if (IsPackageMetadataSaving) return null;
        if (run.Stage < 0)
        {
            if (run.Failure != null) throw new InvalidOperationException("Native initial metadata normalization failed.", run.Failure);
            if (run.Stage != -2 || run.Workspace.HasPackageChanges || documents.HasDirtyDocuments)
                throw new InvalidOperationException("Native setup save did not establish a clean acknowledged workspace.");
            StartProjectSaveParityReplacementCases(run);
            return null;
        }
        if (run.Stage == 0)
        {
            if (run.Continued != 1 || afterDiscard != null || documentReplacementSaving || run.Package.IsDirty)
                throw new InvalidOperationException("A successful replacement continuation must run once after terminal save publication.");
            run.LatestSavedBytes = File.ReadAllBytes(run.Package.FullPath.ToOSPath());
            run.Package.IsDirty = true;
            RequestReplace(() => run.Continued++, true);
            SaveRequestedReplacement();
            // Controlled stale intent injection: completion must not redirect to this new delegate.
            afterDiscard = () => run.Redirected++;
            run.ProgressId = packageMetadataSave?.Id;
            run.Stage = 1; return null;
        }
        if (run.Stage == 1)
        {
            if (run.Continued != 1 || run.Redirected != 0 || documentReplacementSaving)
                throw new InvalidOperationException("A stale replacement intent must never run either old or new action.");
            run.LatestSavedBytes = File.ReadAllBytes(run.Package.FullPath.ToOSPath());
            ClearDocumentReplacementState();
            if (run.ProgressId != null) ClearOperationProgress(run.ProgressId);
            run.Package.IsDirty = true;
            RequestPackageMetadataSave(() => run.Continued++, error => run.Failure = error);
            run.ProgressId = packageMetadataSave?.Id;
            run.CapturedMetadataRevision = run.Metadata.Revision;
            // This is a real canonical revision change after capture, not a fake worker result.
            run.Metadata.RestoreRevision(run.Metadata.AllocateRevision());
            run.Stage = 2; return null;
        }
        if (run.Stage == 2)
        {
            var baselineMatches = File.ReadAllBytes(run.Package.FullPath.ToOSPath()).SequenceEqual(run.LatestSavedBytes);
            if (run.Failure == null || run.Continued != 1 || !run.Package.IsDirty
                || !baselineMatches)
                throw new InvalidOperationException($"Stale native ownership must reject publication while retaining dirty state and original files. failureType={run.Failure?.GetType().FullName ?? "none"}, continued={run.Continued}, nativeIsDirty={run.Package.IsDirty}, fileBaselineMatches={baselineMatches}, metadataRevision={run.Metadata.Revision}, originalMetadataRevision={run.Revision}, capturedSaveRevision={run.CapturedMetadataRevision}, error={run.Failure?.Message ?? "none"}.");
            run.Metadata.RestoreRevision(run.Revision);
            if (run.ProgressId != null) ClearOperationProgress(run.ProgressId);
            RequestPackageMetadataSave(() => run.Continued++, error => run.Failure = error);
            run.Stage = 3; return null;
        }
        if (run.Stage == 3)
        {
            if (run.Package.IsDirty || run.Continued != 2 || IsPackageMetadataSaving || afterDiscard != null)
                throw new InvalidOperationException("The private save fixture must become clean before repeated Save verification.");
            run.CleanSaveBytes = File.ReadAllBytes(run.Package.FullPath.ToOSPath());
            run.CleanSaveHistory = VerificationHistoryTransactions.ToArray();
            run.CleanSaveHistoryPosition = SessionAuthoringHistory!.History.Position;
            run.CleanSaveRevision = run.Metadata.Revision;
            run.Failure = null;
            RequestPackageMetadataSave(() => run.Continued++, error => run.Failure = error);
            run.Stage = 4;
            return null;
        }
        if (run.Package.IsDirty || run.Continued != 3 || run.Failure != null || IsPackageMetadataSaving || afterDiscard != null)
            throw new InvalidOperationException("The private save fixture must finish clean with no live continuation or owner barrier.");
        if (!File.ReadAllBytes(run.Package.FullPath.ToOSPath()).SequenceEqual(run.CleanSaveBytes)
            || run.Metadata.Revision != run.CleanSaveRevision
            || SessionAuthoringHistory!.History.Position != run.CleanSaveHistoryPosition
            || !VerificationHistoryTransactions.SequenceEqual(run.CleanSaveHistory)
            || !ReferenceEquals(SessionAuthoringHistory.Registry.Find(run.Package.FullPath.ToOSPath())?.Document, run.Metadata))
            throw new InvalidOperationException("Repeated clean native Save changed actual metadata bytes, canonical owner/revision or history.");
        run.MetadataLease?.Dispose(); run.MetadataLease = null;
        return new { passed = true, detachedPreparation = true, actorCasPublication = true,
            cancelPreservesDirtyOwnership = true, successAfterTerminalPublication = true,
            staleIntentCannotRedirect = true, staleRevisionRejectsPublication = true,
            repeatedCleanSavePreservesBytesOwnerRevisionAndHistory = true,
            scope = "Actual native save/replacement services on the loaded disposable fixture; no OS input" };
    }

    internal bool RetireProjectSaveParityVerification(ProjectSaveParityRun run)
    {
        if (!ReferenceEquals(workspace, run.Workspace)) throw new InvalidOperationException("The private save fixture ownership changed.");
        if (packageMetadataSave is { } saving)
        {
            if (!ReferenceEquals(saving.Workspace, run.Workspace)) throw new InvalidOperationException("Another workspace owns the save producer.");
            // The detached reader finishes normally; cancellation forbids publication
            // and the existing pump releases every real snapshot/lease phase.
            if (!saving.Published) saving.CancelPublication = true;
            run.ProgressId = saving.Id;
            return false;
        }
        ClearDocumentReplacementState(); documentReplacementSaving = false;
        if (run.ProgressId != null) ClearOperationProgress(run.ProgressId);
        run.Metadata.RestoreRevision(run.Revision);
        // Only this verification's forced dirty marker may be cleared, after actual
        // byte proof; no failed publication is repaired by overwriting project files.
        if (File.ReadAllBytes(run.Package.FullPath.ToOSPath()).SequenceEqual(run.OriginalBytes)) run.Package.IsDirty = false;
        run.MetadataLease?.Dispose(); run.MetadataLease = null;
        return true;
    }
}
