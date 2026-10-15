using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private PackageMetadataSaveRun? packageMetadataSave;
    private bool IsPackageMetadataSaving => packageMetadataSave != null;
    private sealed class PackageMetadataSaveRun(ProjectWorkspace workspace, EditorSessionHistory history, Action? success, Action<Exception>? failure)
    {
        internal readonly string Id = "package-save/" + Guid.NewGuid().ToString("N");
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly EditorSessionHistory History = history;
        internal readonly Action? Success = success;
        internal readonly Action<Exception>? Failure = failure;
        internal readonly List<IDisposable> Owners = [];
        internal readonly List<(EditorAuthoringState State, Guid Generation, long Revision)> States = [];
        internal readonly LoggerResult Diagnostics = new();
        internal PackageMetadataSaveSnapshot? Snapshot;
        internal Task<PreparedPackageMetadataSave>? Preparation;
        internal Exception? Error;
        internal bool Published, Terminal, CancelPublication, DiagnosticsPublished;
    }

    private void RequestPackageMetadataSave(Action? success = null, Action<Exception>? failure = null, bool saveDocuments = false)
    {
        if (IsPackageMetadataSaving || IsPackageDependencyBusy || IsPackagePlatformUpdating)
            throw new InvalidOperationException("A package operation is already running.");
        var source = workspace ?? throw new InvalidOperationException("Open a project first.");
        var history = SessionAuthoringHistory ?? throw new InvalidOperationException("The project has no authoring owner.");
        ValidatePendingPropertyStrings();
        foreach (var project in source.Projects.Where(project => !project.Package.IsReadOnly)) GetProjectBuildSettingsDocument(project, history);
        if (saveDocuments) documents.SaveAll();
        SynchronizeSavedGameSettingsOrientations(); ValidateRecoveredPackageBaselines();
        if (!ReferenceEquals(source, workspace) || !ReferenceEquals(history, SessionAuthoringHistory))
            throw new InvalidOperationException("The project changed while saving pending edits.");
        var run = new PackageMetadataSaveRun(source, history, success, failure);
        packageMetadataSave = run;
        SetOperationProgress(run.Id, "Saving...", "Saving...", null);
        try
        {
            // Preserve canonical file proof and deletion ownership until publication or terminal failure.
            run.Owners.Add(LockAndReconcileSavedAssetItems(source, history));
            run.Owners.Add(assetDeletionHistory.LockPendingDeletionFiles());
            foreach (var package in source.Session.LocalPackages.Where(package => !package.IsReadOnly && package.State >= PackageState.AssetsReady))
                history.Registry.GetOrCreate(package.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(source, package, history));
            foreach (var state in history.Registry.PublishedStates)
            {
                run.Owners.Add(state.AcquireHistory());
                run.States.Add((state, state.Generation, ProjectRemovalDocumentRevision(state.Document)));
            }
            run.Snapshot = source.Session.CaptureMetadataSaveSnapshot();
            var detached = run.Snapshot;
            run.Preparation = Task.Run(() => detached.Prepare(run.Diagnostics));
        }
        catch (PackageMetadataSaveSnapshotCaptureException error)
        { run.Snapshot = error.RetainedSnapshot; run.Error = error; run.Terminal = true; }
        catch (Exception error) { run.Error = error; run.Terminal = true; }
    }

    private void CheckPackageMetadataSaveOwner(PackageMetadataSaveRun run)
    {
        if (!ReferenceEquals(workspace, run.Workspace) || !ReferenceEquals(SessionAuthoringHistory, run.History))
            throw new InvalidOperationException("The project changed while preparing its save.");
        foreach (var (state, generation, revision) in run.States)
            if (!state.IsPublished || state.IsDisposed || state.Generation != generation
                || !ReferenceEquals(run.History.Registry.Find(state.CanonicalPath), state)
                || ProjectRemovalDocumentRevision(state.Document) != revision)
                throw new InvalidOperationException("An authored owner changed while preparing its save.");
        run.Snapshot!.ValidateSourceOwner(run.Workspace.Session);
    }

    private void PumpPackageMetadataSave()
    {
        var run = packageMetadataSave;
        if (run == null || !run.Terminal && run.Preparation?.IsCompleted != true) return;
        if (!run.DiagnosticsPublished)
        {
            foreach (var message in run.Diagnostics.Messages)
            {
                outputLog.Append("Project", message.Type, message.Module, message.Text, message.ExceptionInfo?.ToString());
                AppendOperationProgressLog(run.Id, message.ExceptionInfo == null ? message.Text : message.Text + Environment.NewLine + message.ExceptionInfo);
            }
            run.DiagnosticsPublished = true;
        }
        if (!run.Terminal)
        {
            try
            {
                var prepared = run.Preparation!.GetAwaiter().GetResult();
                ObservePackageDiskConflictPreparation(prepared);
                if (run.CancelPublication) throw new OperationCanceledException("The private save verification was cancelled before publication.");
                CheckPackageMetadataSaveOwner(run);
                SetOperationProgress(run.Id, "Saving...", "Saving...", 0);
                var count = prepared.ProjectMembership.Count + prepared.Removals.Count + prepared.Writes.Count + 1;
                var completed = 0;
                void Advanced() => SetOperationProgress(run.Id, "Saving...", "Saving...", (float)++completed / count);
                foreach (var membership in prepared.ProjectMembership)
                {
                    CheckPackageMetadataSaveOwner(run);
                    SaveOwnedProjectMembership(run.Snapshot!.FindSourceProject(membership.ProjectPath), run.Snapshot.FindPreparedMembershipAsset(membership), membership.Adding);
                    Advanced();
                }
                foreach (var removal in prepared.Removals)
                {
                    CheckPackageMetadataSaveOwner(run);
                    RemoveOwnedAssetFile(run.Snapshot!.FindSourcePackage(removal.PackagePath), removal.FilePath, removal.Kind); Advanced();
                }
                foreach (var write in prepared.Writes)
                {
                    CheckPackageMetadataSaveOwner(run);
                    run.Workspace.Session.UpgradeBackup?.Snapshot(write.PackagePath);
                    SaveOwnedPackageMetadata(run.Snapshot!.FindSourcePackage(write.PackagePath), write.Bytes.ToArray()); Advanced();
                }
                if (prepared.CaptureSolution() is { } solution)
                {
                    if (!string.IsNullOrEmpty(solution.FullPath)) run.Workspace.Session.UpgradeBackup?.Snapshot(solution.FullPath);
                    SaveOwnedSolution(solution);
                }
                CheckPackageMetadataSaveOwner(run);
                run.Workspace.Session.AcknowledgeMetadataSaveSnapshot(run.Snapshot!);
                FinalizeCutSourceDirectories();
                assetDeletionHistory.NotifySaved(); assetRelocationHistory.NotifySaved(); packageFolderHistory.NotifySaved();
                foreach (var state in run.History.Registry.PublishedStates)
                    if (state.Document is PackageMetadataDocument metadata) metadata.NotifyNativeSessionSaved();
                if (!documents.HasDirtyDocuments && !run.Workspace.HasPackageChanges) sessionHistory?.NotifySaveAll(true);
                run.Published = true; Advanced();
                statusMessage = "Package / solution changes saved";
            }
            catch (Exception error) { run.Error = error; }
            run.Terminal = true;
        }
        // Retry actual cleanup phases before releasing the workspace barrier or invoking continuations.
        try
        {
            if (run.Snapshot != null) { run.Snapshot.Dispose(); run.Snapshot = null; }
            while (run.Owners.Count != 0)
            {
                var index = run.Owners.Count - 1;
                run.Owners[index].Dispose(); run.Owners.RemoveAt(index);
            }
        }
        catch (Exception error)
        {
            statusMessage = error.Message;
            CompleteOperationProgress(run.Id, error);
            return;
        }
        packageMetadataSave = null;
        if (run.Error != null)
        {
            CompleteOperationProgress(run.Id, run.Error, run.Error is OperationCanceledException);
            statusMessage = run.Error.Message;
            ReportPackageMetadataSaveContinuationFailure(run, run.Error);
        }
        else if (run.Published)
        {
            try { run.Success?.Invoke(); CompleteOperationProgress(run.Id); }
            catch (Exception error)
            {
                statusMessage = error.Message;
                CompleteOperationProgress(run.Id, error);
                ReportPackageMetadataSaveContinuationFailure(run, error);
            }
        }
    }

    private void ReportPackageMetadataSaveContinuationFailure(PackageMetadataSaveRun run, Exception error)
    {
        try
        {
            if (run.Failure != null) run.Failure(error);
            else outputLog.Append("Project", LogMessageType.Error, "", error.Message, error.ToString());
        }
        catch (Exception continuationError)
        {
            var failure = new AggregateException("The save failure continuation failed.", error, continuationError);
            CompleteOperationProgress(run.Id, failure);
            statusMessage = failure.Message;
            outputLog.Append("Project", LogMessageType.Error, "", failure.Message, failure.ToString());
        }
    }

    private void EnsurePackageMetadataSaveRetired()
    {
        if (IsPackageMetadataSaving) throw new InvalidOperationException("The package save has not reached its terminal publication and cleanup boundary.");
    }
}
