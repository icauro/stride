using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private PlatformUpdatePrompt? packagePlatformPrompt;
    private PlatformUpdateRun? packagePlatformRun;
    private readonly List<PlatformUpdateRun> retiringPlatformUpdates = [];
    private bool openPackagePlatformPrompt, openPackagePlatformConfirmation;
    private TaskCompletionSource? packagePlatformDrain;
    private bool IsPackagePlatformUpdating => packagePlatformRun != null || retiringPlatformUpdates.Count != 0;
    private sealed class PlatformUpdatePrompt(ProjectWorkspace workspace, EditorSessionHistory history, Package package, long selection)
    {
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly EditorSessionHistory History = history;
        internal readonly Package Package = package;
        internal readonly long Selection = selection;
        internal readonly string Path = package.FullPath.ToOSPath();
        internal readonly PackageContainer[] Containers = workspace.Session.Projects.ToArray();
        internal readonly HashSet<PlatformType> Installed = workspace.Projects.Where(project => project.Type == ProjectType.Executable
            && project.Platform != PlatformType.Shared).Select(project => project.Platform).ToHashSet();
        internal readonly HashSet<PlatformType> Selected = workspace.Projects.Where(project => project.Type == ProjectType.Executable
            && project.Platform != PlatformType.Shared).Select(project => project.Platform).ToHashSet();
        internal bool Regenerate;
        internal bool Current(EditorGame game) => ReferenceEquals(game.workspace, Workspace) && ReferenceEquals(game.SessionAuthoringHistory, History)
            && ReferenceEquals(Package.Session, Workspace.Session) && SamePath(Package.FullPath.ToOSPath(), Path)
            && !Package.IsReadOnly && Workspace.Session.Projects.SequenceEqual(Containers) && game.assetBrowser.SelectionVersion == Selection;
    }
    private sealed class PlatformUpdateRun(PlatformUpdateCapture capture, WorkspaceMembershipOwner membership)
    {
        internal readonly PlatformUpdateCapture Capture = capture;
        internal readonly WorkspaceMembershipOwner Membership = membership;
        internal readonly long Revision = membership.Revision;
        internal readonly CancellationTokenSource Cancellation = new();
        internal ProjectLocationRemoval? Dependencies;
        internal Task<PreparedPackagePlatformPublication>? Task;
        internal PlatformUpdateRuntimeTransition? Runtime;
        internal Task? Barrier;
        internal CompiledProjectContent? Content;
        internal bool DependenciesReady, Published, ContentRetained, Released, ViewsRestored;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<ProjectLoadState> Progress = new();
        internal bool Current(EditorGame game) => Capture.Current(game) && Membership.IsCurrent && Membership.Revision == Revision;
        internal void Release()
        {
            if (Released) return;
            if (Task is { IsCompleted: false } || Dependencies?.DependencyTask is { IsCompleted: false })
                throw new InvalidOperationException("The actual platform preparation readers have not retired.");
            if (Task?.Status == TaskStatus.RanToCompletion) Task.Result.Dispose();
            Dependencies?.ReleaseAll(); Capture.Release(); Cancellation.Dispose(); Released = true;
        }
    }
    private Package? SelectedPlatformUpdatePackage()
    {
        if (!CanStartAssetEdit || IsPackagePlatformUpdating || workspace == null) return null;
        var scope = assetBrowser.CaptureNavigationScope();
        return scope.Categories.Length == 0 && scope.Locations.Length == 1 && assetBrowser.FindFolder(scope.Locations[0]) is { Parent: null } folder
            && IsCurrentEditableAssetPackage(folder.Package) && folder.Package.Container is SolutionProject ? folder.Package : null;
    }
    private void DrawPackagePlatformUpdateCommand()
    {
        var command = CaptureProjectLocationCommand(() => SelectedPlatformUpdatePackage() != null, RequestPackagePlatformUpdate);
        if (BeginNativeMenu(EditorLocalization.Widget("Update package", "Menu"), command != null))
        {
            try { if (DrawNativeMenuItem(EditorLocalization.Widget("Update platforms", "Menu"), "", false, command != null)) Execute(command!); }
            finally { Gui.EndMenu(); }
        }
    }
    private void RequestPackagePlatformUpdate()
    {
        if (SelectedPlatformUpdatePackage() is not { } selected || workspace == null || SessionAuthoringHistory == null) return;
        ValidatePendingPropertyStrings(); FinishEntityRename(true); documents.CommitAll();
        if (!ReferenceEquals(SelectedPlatformUpdatePackage(), selected)) return;
        packagePlatformPrompt = new(workspace, SessionAuthoringHistory, selected, assetBrowser.SelectionVersion);
        openPackagePlatformPrompt = true;
    }
    private void DrawPackagePlatformUpdateDialog()
    {
        var title = EditorLocalization.Widget("Select platforms");
        if (openPackagePlatformPrompt) { Gui.OpenPopup(title); openPackagePlatformPrompt = false; }
        Action? action = null;
        Gui.SetNextWindowSize(new Vector2(UiScale(480), 0), ImGuiCond.Appearing);
        if (Gui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize))
        {
            try
            {
                if (packagePlatformPrompt is not { } prompt || !prompt.Current(this))
                { Gui.CloseCurrentPopup(); action = () => packagePlatformPrompt = null; }
                else
                {
                    var ownsKeys = !Gui.GetIO().AppFocusLost && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !Gui.IsAnyItemActive();
                    DrawNativeLabel("Platforms");
                    foreach (var platform in AssetRegistry.SupportedPlatforms)
                    {
                        Gui.PushID(platform.Type.ToString());
                        var selected = prompt.Selected.Contains(platform.Type);
                        Gui.BeginDisabled(platform.Type == PlatformType.Windows);
                        ImTextureRef? image = null;
                        if (icons?.EnumIcon(platform.Type) is { } key) image = icons.Get(key);
                        if (DrawNativeCheckbox(platform.DisplayName ?? platform.Name, ref selected, image, new Vector2(UiScale(16)), UiScale(6)))
                        { if (selected) prompt.Selected.Add(platform.Type); else prompt.Selected.Remove(platform.Type); }
                        Gui.EndDisabled();
                        if (prompt.Installed.Contains(platform.Type) && !selected)
                            DrawSelectableText("##Removed", EditorLocalization.Text("(This will remove the project from the package and disk.)"));
                        if (!platform.IsAvailable)
                            DrawSelectableText("##Unavailable", EditorLocalization.Text("This machine doesn't meet the requirements to build for this platform."));
                        Gui.PopID();
                    }
                    DrawNativeCheckbox(EditorLocalization.Widget("Force regeneration of all platform projects", "Button"), ref prompt.Regenerate);
                    if (DrawNativeButton(EditorLocalization.Widget("OK", "Button")) || ownsKeys && Gui.IsKeyPressed(ImGuiKey.Enter, false))
                    {
                        if (prompt.Selected.Count == 0) action = () => RequestAssetActionMessage(EditorLocalization.Text("You must select at least one platform.", "Message"));
                        else if (prompt.Installed.Except(prompt.Selected).Any()) action = () => openPackagePlatformConfirmation = true;
                        else { Gui.CloseCurrentPopup(); action = () => StartPackagePlatformUpdate(prompt); }
                    }
                    Gui.SameLine();
                    if (DrawNativeButton(EditorLocalization.Widget("Cancel", "Button")) || Gui.IsKeyPressed(ImGuiKey.Escape, false))
                    { Gui.CloseCurrentPopup(); action = () => packagePlatformPrompt = null; }
                }
            }
            finally { Gui.EndPopup(); }
        }
        if (action != null) Execute(action);
        var confirmation = WpfMessageDialogTitle("RemovePackagePlatforms");
        if (openPackagePlatformConfirmation) { Gui.OpenPopup(confirmation); openPackagePlatformConfirmation = false; }
        action = null;
        if (Gui.BeginPopupModal(confirmation, ImGuiWindowFlags.AlwaysAutoResize))
        {
            try
            {
                if (packagePlatformPrompt is not { } prompt || !prompt.Current(this))
                { Gui.CloseCurrentPopup(); action = () => packagePlatformPrompt = null; }
                else
                {
                    DrawMessageDialogBody("##PlatformsRemoval", string.Format(EditorLocalization.Text("Are you sure you want to remove these {0} platform(s) from the package?", "Message"),
                        prompt.Installed.Except(prompt.Selected).Count()), EditorMessageDialogImage.Question);
                    if (DrawNativeButton(EditorLocalization.Widget("Remove", "Button")))
                    { Gui.CloseCurrentPopup(); action = () => StartPackagePlatformUpdate(prompt); }
                    Gui.SameLine();
                    if (DrawNativeButton(EditorLocalization.Widget("Cancel", "Button")) || Gui.IsKeyPressed(ImGuiKey.Escape, false))
                    { Gui.CloseCurrentPopup(); action = () => openPackagePlatformPrompt = true; }
                }
            }
            finally { Gui.EndPopup(); }
        }
        if (action != null) Execute(action);
        DrawPackagePlatformProgress();
    }
    private void StartPackagePlatformUpdate(PlatformUpdatePrompt prompt)
    {
        if (!ReferenceEquals(packagePlatformPrompt, prompt) || !prompt.Current(this) || !CanStartAssetEdit || IsPackagePlatformUpdating) return;
        var capture = CapturePackagePlatformUpdate(prompt.Package, prompt.Selected.ToArray(), prompt.Regenerate);
        var removing = capture.Plan.ProjectsToRemove.Where(removal => removal.Project != null).Select(removal => (PackageContainer)removal.Project!).ToArray();
        if (capture.Plan.ProjectsToRemove.Count == 0 && capture.Plan.PlatformsToInstantiate.Count == 0)
        {
            packagePlatformPrompt = null;
            try { capture.Release(); }
            catch { if (!packageCommandCleanup.Contains(capture.Release)) packageCommandCleanup.Add(capture.Release); throw; }
            return;
        }
        PlatformUpdateRun? run = null;
        try
        {
            run = new(capture, GetWorkspaceRemovalMembership(removing)); packagePlatformRun = run; packagePlatformPrompt = null; BeginPackagePlatformProgress();
            var nonReplacing = capture.Plan.ProjectsToRemove.Where(removal => removal.Project != null
                && !capture.Plan.PlatformsToInstantiate.Contains(removal.Platform)).Select(removal => (PackageContainer)removal.Project!).ToArray();
            run.Dependencies = new(capture.Workspace, nonReplacing, run.Membership, assetBrowser.SelectionVersion);
            PrepareProjectRemovalDependencyChecks(run.Dependencies, () => run.Current(this), () => run.DependenciesReady = true);
        }
        catch (Exception error)
        {
            if (run != null) { CompletePackagePlatformProgress(null, error, false); RetirePackagePlatformRun(run); }
            else
            {
                try { capture.Release(); }
                catch { if (!packageCommandCleanup.Contains(capture.Release)) packageCommandCleanup.Add(capture.Release); }
            }
            throw;
        }
    }
    private void BeginPackagePlatformPreparation(PlatformUpdateRun run)
    {
        var current = run.Capture.Workspace.Session.CurrentProject;
        var replacingActive = current != null && run.Capture.Plan.ProjectsToRemove.Any(removal => ReferenceEquals(removal.Project, current));
        var targetPath = replacingActive && run.Capture.Plan.PlatformsToInstantiate.Contains(current!.Platform) ? current.FullPath.ToOSPath()
            : replacingActive ? ((SolutionProject)run.Capture.Plan.GameLibrary.Container).FullPath.ToOSPath() : current?.FullPath.ToOSPath() ?? "";
        var compile = replacingActive && projectContent != null;
        var token = run.Cancellation.Token;
        run.Task = Task.Run(() => PreparedPackagePlatformPublication.Prepare(run.Capture.Snapshot, targetPath, compile, token,
            progress => run.Progress.Enqueue(progress)), token);
    }
    private void PumpPackagePlatformUpdate()
    {
        foreach (var retired in retiringPlatformUpdates.ToArray())
            if (retired.Task is not { IsCompleted: false } && retired.Dependencies?.DependencyTask is not { IsCompleted: false }
                && retired.Barrier is not { IsCompleted: false })
                Execute(() =>
                {
                    if (packagePlatformDrain == null && !retired.Published && !retired.ViewsRestored && retired.Runtime?.CanRestoreBefore == true)
                    { retired.Barrier?.GetAwaiter().GetResult(); EnsureEditorGpuRetirementCompleted(); retired.Runtime.RestoreViews(); retired.ViewsRestored = true; }
                    retired.Release(); retiringPlatformUpdates.Remove(retired);
                });
        if (!IsPackagePlatformUpdating && packagePlatformDrain is { } drain)
        { packagePlatformDrain = null; drain.TrySetResult(); }
        if (packagePlatformRun is not { } run) return;
        while (run.Progress.TryDequeue(out var progress))
        {
            if (progress.Diagnostic != null) outputLog.Append("Build", LogMessageType.Info, "", progress.Detail);
            statusMessage = progress.Detail; AppendPackagePlatformProgress(progress);
        }
        if (run.Cancellation.IsCancellationRequested || !run.Current(this)) { RetirePackagePlatformRun(run); return; }
        try
        {
            if (!run.DependenciesReady)
            {
                if (run.Dependencies?.DependencyTask?.IsCompleted != true) return;
                CompleteProjectRemovalDependencyChecks(run.Dependencies, () => run.Current(this)); run.DependenciesReady = true;
            }
            if (run.Task == null) { BeginPackagePlatformPreparation(run); return; }
            if (!run.Task.IsCompleted) return;
            var prepared = run.Task.GetAwaiter().GetResult(); run.Cancellation.Token.ThrowIfCancellationRequested();
            if (!run.Current(this)) throw new InvalidOperationException("The package changed while preparing platform projects.");
            if (run.Barrier == null)
            {
                var current = run.Capture.Workspace.Session.CurrentProject;
                if (current != null && run.Capture.Plan.ProjectsToRemove.Any(removal => ReferenceEquals(removal.Project, current)))
                {
                    var target = prepared.Membership.Projects.OfType<SolutionProject>().FirstOrDefault(project => SamePath(project.FullPath.ToOSPath(), prepared.TargetPath))
                        ?? run.Capture.Workspace.Projects.Single(project => SamePath(project.FullPath.ToOSPath(), prepared.TargetPath));
                    run.Content = prepared.Output.Length == 0 ? null : new CompiledProjectContent(this, prepared.Output);
                    run.Runtime = new(this, run.Capture.Workspace, target, run.Content, prepared.Output);
                    run.Barrier = run.Runtime.RetireViews();
                }
                else run.Barrier = Task.CompletedTask;
            }
            if (!run.Barrier.IsCompleted) return;
            run.Barrier.GetAwaiter().GetResult(); Execute(() =>
            {
                try { PublishPackagePlatformUpdate(run, prepared); }
                catch (Exception error) { CompletePackagePlatformProgress(null, error, false); RetirePackagePlatformRun(run); throw; }
            });
        }
        catch (OperationCanceledException) { CompletePackagePlatformProgress(null, null, true); RetirePackagePlatformRun(run); }
        catch (Exception error)
        { outputLog.Append("Project", LogMessageType.Error, "", error.Message, error.ToString()); statusMessage = error.Message; CompletePackagePlatformProgress(null, error, false); RetirePackagePlatformRun(run); }
    }
    private void PublishPackagePlatformUpdate(PlatformUpdateRun run, PreparedPackagePlatformPublication prepared)
    {
        if (!ReferenceEquals(packagePlatformRun, run) || !run.Current(this)) throw new InvalidOperationException("The native platform publication intent changed.");
        ValidatePendingPropertyStrings();
        if (!run.Current(this)) throw new InvalidOperationException("The native platform publication owner changed.");
        run.Capture.ValidateNativeProjectBaselines();
        prepared.Files.Mirror.ValidateExternalProjectInputs();
        var history = run.Capture.History; var state = history.Registry.Find(run.Membership.Document.Path)!;
        var files = new PackagePlatformFilesOperation(prepared.Files);
        using var capture = history.CaptureOperationsAtomic(() =>
        {
            using var recording = history.CreateTransaction("Update platforms");
            ApplyProjectRemovalDependencies(run.Dependencies!);
            history.PushOperation(files, state); files.Change(true);
            prepared.Membership.AcceptPlatformFileOwnership(files.HasPublishedFileOwnership);
            if (run.Runtime != null)
            {
                history.PushOperation(new PlatformUpdateSuspendOperation(run.Runtime), state);
                run.ContentRetained = true;
                if (run.Content != null && !retainedActiveProjectContent.Contains(run.Content)) retainedActiveProjectContent.Add(run.Content);
                run.Runtime.Suspend(true);
            }
            var removing = run.Capture.Plan.ProjectsToRemove.Where(removal => removal.Project != null).Select(removal => (PackageContainer)removal.Project!).ToArray();
            run.Membership.ReplacePlatforms(removing, prepared.Membership, () =>
            { assetBrowser.RefreshEntries(run.Capture.Workspace); ClearAssetReferenceState(); ClearPackagePropertiesState(); StopScriptSemantics(); buildWorkspace = null; }, SynchronizeRemovedPackageViews);
            if (run.Runtime != null) { history.PushOperation(new PlatformUpdateActivationOperation(run.Runtime), state); run.Runtime.Complete(true); run.ContentRetained = true; }
        });
        capture.Publish(history, "Update platforms"); run.Published = true; CompletePackagePlatformProgress(prepared.Files.Messages, null, false);
        if (run.Runtime != null && history.Stack.NextUndoTransaction is { } transaction) emptyWorkspaceTransactions[transaction.Id] = run.Runtime;
        run.Capture.Workspace.RefreshAssets(); RetirePackagePlatformRun(run);
    }
    private void RetirePackagePlatformRun(PlatformUpdateRun run)
    {
        run.Cancellation.Cancel(); run.Dependencies?.DependencyCancellation?.Cancel();
        if (run.Runtime != null && run.Barrier == null) run.Barrier = BeginEditorGpuRetirementAsync();
        if (ReferenceEquals(packagePlatformRun, run)) packagePlatformRun = null;
        if (!retiringPlatformUpdates.Contains(run)) retiringPlatformUpdates.Add(run);
        if (run.Content != null && !run.ContentRetained)
        { RetireEditorPreview(run.Content); run.Content = null; }
    }
    private Task BeginPackagePlatformDrainAsync()
    {
        // Completion belongs to the actual actor pump after all native readers,
        // publication results and canonical leases have retired. No background
        // continuation releases authored state or manufactures an idle signal.
        pendingPlatformViewRestores.Clear(); // Protected teardown owns presentation now; history/bundle owners remain retained.
        packagePlatformDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = packagePlatformDrain.Task;
        StopPackagePlatformUpdate();
        return task;
    }
    private void StopPackagePlatformUpdate()
    {
        packagePlatformPrompt = null; openPackagePlatformPrompt = openPackagePlatformConfirmation = false;
        platformProgressPending = platformProgressKeepOpen = false;
        if (packagePlatformRun is { } run) RetirePackagePlatformRun(run);
    }
}
