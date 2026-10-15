using System.Collections.Concurrent;
using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Packages;
using Stride.Core.Transactions;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private PackageDependencyRestoreRun? packageDependencyRestore;
    private bool showPackageDependencyRestoreProgress;
    private bool IsPackageDependencyUpdating => packageDependencyRestore != null;
    private sealed class PackageDependencyRestoreRun(ProjectWorkspace workspace, EditorSessionHistory history, SolutionProject project)
    {
        public readonly ProjectWorkspace Workspace = workspace;
        public readonly EditorSessionHistory History = history;
        public readonly SolutionProject Project = project;
        public readonly LoggerResult Log = new();
        public readonly CancellationTokenSource Cancellation = new();
        public readonly NativeUpgradePrompt Upgrades = new();
        public string? CurrentMessage;
        public readonly ConcurrentQueue<(Action Apply, TaskCompletionSource Completion)> Pending = new();
        public readonly List<EditorOperationCapture> Captures = [];
        public Task? Task;
        public bool RestoreFailed;
        public bool Recovering, Published, Recovered;
        public ProjectBuildSettingsDocument.ProjectRestorePersistence? Persistence;
        public EventHandler<MessageLoggedEventArgs>? LogHandler;
        public Task Dispatch(Action apply)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Enqueue((apply, completion)); return completion.Task;
        }
    }
    private void StartPackageDependencyRestore(SolutionProject project, Action<PackageDependencies> change)
    {
        if (IsAuthoringPipelineUpdating || projectLoading != null || assetPreviewBuild != null || IsUserProjectBuilding
            || closeRequested || closePromptOpen || exitApproved || workspace == null || SessionAuthoringHistory == null)
            throw new InvalidOperationException("A package operation is already running.");
        var run = new PackageDependencyRestoreRun(workspace, SessionAuthoringHistory, project);
        var service = PackageDependencyService();
        try
        {
            var owner = run.History.Registry.GetOrCreate(project.FullPath.ToOSPath(), null, () => new ProjectBuildSettingsDocument(project, run.History)).Document;
            run.Persistence = owner.BeginDependencyRestorePersistence();
            run.Captures.Add(run.History.CaptureOperationsAtomic(() => change(service)));
            run.LogHandler = (_, args) =>
            {
                run.CurrentMessage = args.Message.Text;
                outputLog.Append("Project", args.Message.Type, args.Message.Module, args.Message.Text, args.Message.ExceptionInfo?.ToString());
            };
            run.Log.MessageLogged += run.LogHandler;
            packageDependencyRestore = run;
            showPackageDependencyRestoreProgress = true;
            run.Task = service.RestoreAsync(project, run.Log,
                () => run.Dispatch(() =>
                {
                    run.Persistence.Save();
                }), run.Dispatch, async path =>
                {
                    Package? package = null;
                    await run.Dispatch(() =>
                    {
                        run.Cancellation.Token.ThrowIfCancellationRequested();
                        var existing = run.Workspace.Session.Packages.FirstOrDefault(current => current.FullPath == path);
                        if (existing?.State >= PackageState.AssetsReady) package = existing;
                    });
                    if (package != null) return package;
                    run.CurrentMessage = path.ToOSPath();
                    var prepared = await Task.Run(() => PreparedSolutionProjects.Prepare(path.ToOSPath(), run.Cancellation.Token, dependencyPackage: true, upgradePrompt: run.Upgrades));
                    try
                    {
                        await run.Dispatch(() =>
                        {
                            run.Cancellation.Token.ThrowIfCancellationRequested();
                            var adoption = new DependencyPackageAdoption(run.Workspace, run.History, prepared);
                            try
                            {
                                adoption.Apply();
                                var projectOwner = run.History.Registry.Find(run.Project.FullPath.ToOSPath())
                                    ?? throw new InvalidOperationException("The dependency project has no canonical owner.");
                                package = prepared.Projects.Select(container => container.Package).Single(current =>
                                    string.Equals(prepared.OriginalPackagePath(current), path.ToOSPath(), StringComparison.OrdinalIgnoreCase));
                                if (!run.Workspace.Session.Packages.Contains(package)) throw new InvalidOperationException("The prepared library root was not adopted.");
                                run.History.PushOperation(adoption, [projectOwner, .. adoption.States]);
                                prepared.IsRetained = true;
                            }
                            catch (Exception failure)
                            {
                                try { adoption.Restore(); adoption.Release(); }
                                catch (Exception recovery)
                                {
                                    prepared.IsRetained = true;
                                    run.History.RetainFailedInitialOperation(adoption, adoption.States.ToArray());
                                    throw new AggregateException("Dependency adoption and native membership restoration failed.", failure, recovery);
                                }
                                throw;
                            }
                        });
                        return package ?? throw new InvalidOperationException("The prepared native package was not published.");
                    }
                    finally { if (!prepared.IsRetained) prepared.Dispose(); }
                }, run.Cancellation.Token);
        }
        catch
        {
            foreach (var capture in run.Captures.AsEnumerable().Reverse()) { capture.RestoreApplied(); capture.Dispose(); }
            run.Persistence?.Dispose();
            run.Cancellation.Dispose();
            run.Upgrades.Dispose();
            packageDependencyRestore = null;
            throw;
        }
    }
    private void PumpPackageDependencyRestore()
    {
        var run = packageDependencyRestore;
        if (run == null) return;
        while (run.Pending.TryDequeue(out var pending))
        {
            try
            {
                if (!ReferenceEquals(workspace, run.Workspace) || !ReferenceEquals(SessionAuthoringHistory, run.History))
                    throw new InvalidOperationException("The project changed while restoring dependencies.");
                run.Captures.Add(run.History.CaptureOperationsAtomic(() =>
                {
                    // A capture retains applied operations, but it does not open
                    // the native stack transaction required by PushOperation.
                    using var transaction = run.History.CreateTransaction("Restore package dependencies");
                    pending.Apply();
                }));
                pending.Completion.SetResult();
            }
            catch (Exception error) { pending.Completion.SetException(error); }
        }
        if (run.Task?.IsCompleted != true || run.RestoreFailed) return;
        try
        {
            if (run.Recovering)
            {
                RecoverPackageDependencyRestore(run);
                return;
            }
            if (run.Published) { CompletePackageDependencyRestore(run); return; }
            run.Task.GetAwaiter().GetResult();
            using (var transaction = run.History.CreateTransaction("Add dependency to package '" + run.Project.Package.Meta.Name + "'"))
                run.History.PushOperation(new CompositeOperation(run.Captures.SelectMany(capture => capture.Entries).Select(entry => entry.Operation)),
                    run.Captures.SelectMany(capture => capture.Participants).Distinct().ToArray());
            run.Published = true;
            CompletePackageDependencyRestore(run);
        }
        catch (Exception error)
        {
            try
            {
                if (run.Published) throw;
                run.Recovering = true;
                RecoverPackageDependencyRestore(run);
            }
            catch (Exception recovery)
            {
                run.RestoreFailed = true; // Keep every actual operation lease until recovery succeeds.
                outputLog.Append("Project", LogMessageType.Error, "", recovery.Message, recovery.ToString());
            }
            if (error is OperationCanceledException)
            {
                outputLog.Append("Project", LogMessageType.Info, "", "Cancelled", null);
                statusMessage = "Cancelled";
            }
            else
            {
                outputLog.Append("Project", LogMessageType.Error, "", error.Message, error.ToString());
                statusMessage = error.Message;
            }
        }
    }
    private void RecoverPackageDependencyRestore(PackageDependencyRestoreRun run)
    {
        if (!run.Recovered)
        {
            foreach (var capture in run.Captures.AsEnumerable().Reverse()) capture.RestoreApplied();
            run.Persistence!.RestoreOriginalFile();
            run.Recovered = true;
        }
        CompletePackageDependencyRestore(run);
    }
    private void CompletePackageDependencyRestore(PackageDependencyRestoreRun run)
    {
        run.Log.MessageLogged -= run.LogHandler;
        run.Workspace.RefreshAssets(); assetBrowser.RefreshEntries(run.Workspace);
        assetReferences.Clear(); InvalidateAssetThumbnails(null);
        foreach (var capture in run.Captures) capture.Dispose();
        run.Captures.Clear(); packageDependencyRestore = null;
        run.Persistence?.Dispose();
        run.Cancellation.Dispose();
        run.Upgrades.Dispose();
    }
    private void DrawPackageDependencyRestoreProgress()
    {
        if (showPackageDependencyRestoreProgress) { Gui.OpenPopup("Importing project..."); showPackageDependencyRestoreProgress = false; }
        if (!Gui.BeginPopupModal("Importing project...", ImGuiWindowFlags.AlwaysAutoResize))
        { packageDependencyRestore?.Upgrades.Draw(); return; }
        if (packageDependencyRestore == null) Gui.CloseCurrentPopup();
        else if (packageDependencyRestore.RestoreFailed)
        {
            if (Gui.Button("Retry")) packageDependencyRestore.RestoreFailed = false;
        }
        else
        {
            Gui.ProgressBar(-(float)Gui.GetTime(), new Vector2(420, 0), "");
            if (packageDependencyRestore.CurrentMessage is { } message)
            {
                Gui.PushTextWrapPos(Gui.GetCursorPosX() + 420); Gui.TextUnformatted(message); Gui.PopTextWrapPos();
            }
            Gui.BeginDisabled(packageDependencyRestore.Cancellation.IsCancellationRequested);
            if (Gui.Button(EditorLocalization.Widget("Cancel", "Button"))) packageDependencyRestore.Cancellation.Cancel();
            Gui.EndDisabled();
        }
        Gui.EndPopup();
        packageDependencyRestore?.Upgrades.Draw();
    }
    private void ClearPackageDependencyRestoreState()
    {
        if (IsPackageDependencyUpdating) throw new InvalidOperationException("The package operation has not finished.");
        showPackageDependencyRestoreProgress = false;
    }
    private void FinishPackageDependencyRestoreForShutdown()
    {
        packageDependencyRestore?.Cancellation.Cancel();
        // Forced host teardown must continue draining its owner-thread dispatcher;
        // blocking on Task alone would deadlock its pending publication callbacks.
        while (packageDependencyRestore is { } run)
        {
            PumpPackageDependencyRestore();
            if (packageDependencyRestore == null) break;
            if (run.RestoreFailed) throw new InvalidOperationException("Package operation recovery must finish before closing the editor.");
            try { run.Task?.Wait(25); }
            catch (AggregateException) { /* The next pump reports and compensates the completed task. */ }
        }
    }
}
