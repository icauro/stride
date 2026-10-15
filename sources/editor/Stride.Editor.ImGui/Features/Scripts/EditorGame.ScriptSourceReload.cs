using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorSessionHistory? sourceReloadHistory;
    private readonly Dictionary<string, long> pendingSourceReloadProjects = new(StringComparer.OrdinalIgnoreCase);
    private long sourceReloadRevision, sourcePromptRevision = -1, sourceReloadChangedAt, acceptedSourceReloadRevision = -1;
    private bool sourceReloadPromptOpen, sourceReloadDontAsk;
    private sealed record ScriptSourceBuildBatch(DesktopProjectBuildResult[] Results, KeyValuePair<string, Exception>[] Failures, ScriptRuntimeCompilation[] NativeResults);
    private ProjectWorkspace? sourceCompilationWorkspace;
    private EditorSessionHistory? sourceCompilationHistory;
    private Task<ScriptSourceBuildBatch>? scriptRecompileTask;
    private CancellationTokenSource? scriptRecompileCancellation;
    private KeyValuePair<string, long>[] recompilingSourceProjects = [];

    private void QueueScriptSourceReload(ScriptSourceDocument source)
    {
        if (!ReferenceEquals(source.SessionHistory, sessionHistory) || !source.IsCSharpSource || source.ProjectPath == null) return;
        QueueScriptSourceProject(source.ProjectPath);
    }
    private void QueueScriptSourceProject(string projectPath)
    {
        if (workspace == null || !workspace.Session.Packages.Where(package => !package.IsReadOnly).SelectMany(package => package.LoadedAssemblies)
            .Any(owner => owner.ProjectReference?.Location != null && SamePath(owner.ProjectReference.Location.ToOSPath(), projectPath))
            && !(liveExecutableBuild is { } executable && SamePath(executable.ProjectPath, projectPath))) return;
        pendingSourceReloadProjects[Path.GetFullPath(projectPath)] = ++sourceReloadRevision;
        sourceReloadChangedAt = Environment.TickCount64;
    }
    private void PumpScriptSourceReload()
    {
        foreach (var pair in changedScriptSourcePaths.Where(pair => Environment.TickCount64 - pair.Value.At >= 650).ToArray())
            if (changedScriptSourcePaths.TryRemove(pair.Key, out var change)) QueueScriptSourceProject(change.Project);
        if (!ReferenceEquals(sourceReloadHistory, sessionHistory))
        {
            if (sourceReloadHistory != null) sourceReloadHistory.ScriptSourceSaved = null;
            sourceReloadHistory = sessionHistory;
            if (sourceReloadHistory != null) sourceReloadHistory.ScriptSourceSaved = QueueScriptSourceReload;
        }
        if (scriptRecompileTask?.IsCompleted == true)
        {
            var completed = scriptRecompileTask; scriptRecompileTask = null;
            scriptRecompileCancellation?.Dispose(); scriptRecompileCancellation = null;
            ScriptSourceBuildBatch? batch = null;
            try
            {
                batch = completed.GetAwaiter().GetResult();
                if (!ReferenceEquals(workspace, sourceCompilationWorkspace) || !ReferenceEquals(sessionHistory, sourceCompilationHistory)) throw new OperationCanceledException("The compilation session changed.");
                if (batch.Failures.Any(failure => failure.Value is OperationCanceledException)) throw new OperationCanceledException("Build cancelled");
                ApplyRuntimeCompilations(batch.NativeResults);
                foreach (var result in batch.NativeResults)
                {
                    var consumed = recompilingSourceProjects.FirstOrDefault(pair => SamePath(pair.Key, result.ProjectPath));
                    if (pendingSourceReloadProjects.TryGetValue(result.ProjectPath, out var revision) && revision <= consumed.Value) pendingSourceReloadProjects.Remove(result.ProjectPath);
                }
                foreach (var result in batch.Results)
                {
                    QueueScriptAssemblyReload(result);
                    if (liveExecutableBuild is { } executable && SamePath(executable.ProjectPath, result.ProjectPath)) InstallLiveExecutable(result);
                    var consumed = recompilingSourceProjects.FirstOrDefault(pair => SamePath(pair.Key, result.ProjectPath));
                    if (pendingSourceReloadProjects.TryGetValue(result.ProjectPath, out var revision) && revision <= consumed.Value)
                        pendingSourceReloadProjects.Remove(result.ProjectPath);
                }
                foreach (var failure in batch.Failures)
                    outputLog.Append("Build", failure.Value is OperationCanceledException ? LogMessageType.Info : LogMessageType.Error,
                        "Reload assemblies", failure.Key + Environment.NewLine + failure.Value);
            }
            catch (OperationCanceledException) { outputLog.Append("Build", LogMessageType.Info, "Reload assemblies", "Build cancelled"); }
            catch (Exception error) { outputLog.Append("Build", LogMessageType.Error, "Reload assemblies", error.ToString()); }
            finally
            {
                if (batch != null) foreach (var result in batch.NativeResults) result.Artifacts.Dispose();
                recompilingSourceProjects = []; sourceCompilationWorkspace = null; sourceCompilationHistory = null;
                ScriptRuntimeArtifacts.RetryCleanup();
            }
        }
        if (workspace == null || sessionHistory == null || userProjectBuild != null || scriptRecompileTask != null
            || projectLoading != null || IsAuthoringPipelineUpdating || IsAssetPreviewUpdating || !IsActiveForTick && !IsLiveScripting
            || sessionHistory.Stack.TransactionInProgress || sessionHistory.Stack.RollInProgress || sessionHistory.IsCapturingOperations || sessionHistory.IsCompensating
            || closeRequested || closePromptOpen || exitApproved || pendingSourceReloadProjects.Count == 0
            || Environment.TickCount64 - sourceReloadChangedAt < 650) return;
        if (acceptedSourceReloadRevision < 0 && sourcePromptRevision != sourceReloadRevision)
        {
            sourcePromptRevision = sourceReloadRevision;
            if (IsLiveScripting) acceptedSourceReloadRevision = sourceReloadRevision;
            else if (preferences?.Current.AskBeforeReloadingAssemblies != false) sourceReloadPromptOpen = true;
            else if (preferences.Current.AutoReloadAssemblies) acceptedSourceReloadRevision = sourceReloadRevision;
        }
        if (acceptedSourceReloadRevision < 0 || sourceReloadPromptOpen) return;
        var accepted = acceptedSourceReloadRevision; acceptedSourceReloadRevision = -1;
        recompilingSourceProjects = pendingSourceReloadProjects.Where(pair => pair.Value <= accepted).ToArray();
        if (recompilingSourceProjects.Length == 0) return;
        AuthoredProjectSnapshot[] snapshots;
        RuntimeAssemblyOwner[] runtimeOwners;
        Dictionary<string, string>? nativeFrameworks;
        try
        {
            snapshots = CaptureScriptProjectSnapshots();
            runtimeOwners = CaptureRuntimeAssemblyOwners();
            nativeFrameworks = snapshots.Length == 0 ? null : recompilingSourceProjects.ToDictionary(pair => pair.Key,
                pair => ResolveAuthoredRuntimeFramework(pair.Key, runtimeOwners), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error)
        {
            outputLog.Append("Build", LogMessageType.Error, "Reload assemblies", error.ToString());
            recompilingSourceProjects = []; return;
        }
        var projects = recompilingSourceProjects.Select(pair => (Path: pair.Key,
            Outputs: workspace.Session.Packages.SelectMany(package => package.LoadedAssemblies)
                .Where(owner => owner.ProjectReference?.Location != null && SamePath(owner.ProjectReference.Location.ToOSPath(), pair.Key))
                .Select(owner => owner.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
            LiveFramework: liveExecutableBuild is { } executable && SamePath(executable.ProjectPath, pair.Key) ? executable.Framework : null)).ToArray();
        var solution = workspace.OpenPath; scriptRecompileCancellation = new(); var token = scriptRecompileCancellation.Token;
        sourceCompilationWorkspace = workspace; sourceCompilationHistory = sessionHistory;
        scriptRecompileTask = Task.Run(async () =>
        {
            var results = new List<DesktopProjectBuildResult>();
            var failures = new List<KeyValuePair<string, Exception>>();
            var nativeResults = new List<ScriptRuntimeCompilation>();
            foreach (var project in projects)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (nativeFrameworks != null)
                    {
                        nativeResults.Add(await CompileAuthoredRuntimeProject(project.Path, nativeFrameworks[project.Path], snapshots, runtimeOwners, token));
                        continue;
                    }
                    var framework = project.LiveFramework ?? await DesktopProjectBuild.ResolveEditorBuildFramework(project.Path, project.Outputs, token);
                    // WPF's assembly-reload build uses the real Debug project target.
                    results.Add(await DesktopProjectBuild.Build(project.Path, "Debug", framework, solution, token, RecordBuildOutput));
                }
                catch (Exception error) { failures.Add(new(project.Path, error)); if (error is OperationCanceledException) break; }
            }
            return new ScriptSourceBuildBatch(results.ToArray(), failures.ToArray(), nativeResults.ToArray());
        }, token);
    }
    private void DrawScriptSourceReloadPrompt()
    {
        var confirmationTitle = WpfMessageDialogTitle("ReloadAssembliesConfirmation");
        if (sourceReloadPromptOpen && !Gui.IsAnyItemActive()
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            Gui.OpenPopup(confirmationTitle);
        if (!Gui.BeginPopupModal(confirmationTitle, ImGuiWindowFlags.AlwaysAutoResize)) return;
        Gui.TextUnformatted(EditorLocalization.Text("Some game code files have been modified. Do you want to reload the assemblies?", "Message"));
        Gui.Checkbox(EditorLocalization.Widget("Don't ask again"), ref sourceReloadDontAsk);
        if (Gui.Button(EditorLocalization.Widget("Reload", "Button"))) Answer(true);
        Gui.SetItemDefaultFocus();
        Gui.SameLine();
        var declineReload = Gui.Button(EditorLocalization.Widget("Don't reload", "Button"));
        if (Controls.Enabled) Controls.Record("script-reload/dont-reload");
        TraceWorkflowUiAction("script-reload/dont-reload", declineReload);
        if (declineReload) Answer(false);
        Gui.EndPopup();
        void Answer(bool reload)
        {
            if (sourceReloadDontAsk && preferences != null)
            {
                preferences.Current.AskBeforeReloadingAssemblies = false;
                preferences.Current.AutoReloadAssemblies = reload;
                SaveEditorPreferences();
            }
            sourceReloadDontAsk = false; sourceReloadPromptOpen = false;
            if (reload) acceptedSourceReloadRevision = sourceReloadRevision;
            Gui.CloseCurrentPopup();
        }
    }
    private void StopScriptSourceReload()
    {
        if (sourceReloadHistory != null) sourceReloadHistory.ScriptSourceSaved = null;
        sourceReloadHistory = null;
        if (scriptRecompileTask != null)
            _ = scriptRecompileTask.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) foreach (var result in task.Result.NativeResults) result.Artifacts.Dispose();
                else if (task.IsFaulted) _ = task.Exception;
            }, TaskScheduler.Default);
        DetachBuildTask(scriptRecompileTask, scriptRecompileCancellation);
        scriptRecompileTask = null; scriptRecompileCancellation = null; recompilingSourceProjects = [];
        pendingSourceReloadProjects.Clear(); sourceReloadPromptOpen = sourceReloadDontAsk = false;
        sourcePromptRevision = acceptedSourceReloadRevision = -1; sourceReloadRevision = 0;
        sourceCompilationWorkspace = null; sourceCompilationHistory = null;
    }
}
