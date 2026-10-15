using System.Diagnostics;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ScriptSemanticWorkspace? scriptSemantics;
    private ProjectWorkspace? semanticProjectWorkspace;
    private string semanticProjectPath = "", semanticConfiguration = "", semanticFramework = "";
    private readonly Dictionary<ScriptSourceDocument, long> observedScriptVersions = [];
    private ScriptSourceDocument? semanticTarget;
    private Task<ScriptSemanticResult>? scriptSemanticTask;
    private CancellationTokenSource? scriptSemanticCancellation;
    private ScriptSemanticResult? scriptSemanticResult;
    private long scriptSemanticGeneration, runningSemanticGeneration, observedSemanticEpoch = -1, semanticChangedAt;
    private bool semanticPending;

    private void PumpScriptSemantics()
    {
        PumpScriptCodeAssistance();
        var target = documents.Active?.Document as ScriptSourceDocument;
        if (target?.IsCSharpSource != true) target = null;
        var selectedBuildContext = ReferenceEquals(buildWorkspace, workspace) && buildTarget != null && buildMetadata != null;
        var project = selectedBuildContext ? buildTarget!.FullPath.ToOSPath() : workspace?.BuildProjectPath ?? "";
        var configuration = selectedBuildContext && buildMetadata!.Configurations.Contains(buildConfiguration) ? buildConfiguration : "Debug";
        var framework = selectedBuildContext && buildMetadata!.Frameworks.Contains(buildFramework) ? buildFramework : "";
        var contextChanged = !ReferenceEquals(semanticProjectWorkspace, workspace) || semanticProjectPath != project
            || semanticConfiguration != configuration || semanticFramework != framework;
        if (contextChanged)
        {
            StopScriptSemantics(); semanticProjectWorkspace = workspace; semanticProjectPath = project;
            semanticConfiguration = configuration; semanticFramework = framework;
            if (workspace != null && Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                scriptSemantics = new ScriptSemanticWorkspace(project, configuration, framework);
        }
        if (scriptSemantics == null) return;
        try
        {
            scriptSemantics.SetProjectSnapshots(CaptureScriptProjectSnapshots()); scriptProjectSnapshotError = null;
        }
        catch (Exception error)
        {
            scriptSemanticCancellation?.Cancel(); scriptAssistCancellation?.Cancel();
            if (scriptProjectSnapshotError != error.ToString())
            { scriptProjectSnapshotError = error.ToString(); outputLog.Append("Scripts", LogMessageType.Error, "Project context", scriptProjectSnapshotError); }
            return;
        }
        scriptSemantics.SetProjectMembership(CaptureScriptProjectMembership());
        foreach (var source in documents.AuthoredDocuments.OfType<ScriptSourceDocument>()) source.PruneRetiredReferenceContexts();
        scriptSemantics.SetReferenceContexts(documents.AuthoredDocuments.OfType<ScriptSourceDocument>()
            .SelectMany(source => source.CaptureReferenceContexts()).OrderBy(context => context.Order).ToArray());
        var changed = contextChanged || !ReferenceEquals(semanticTarget, target) || observedSemanticEpoch != scriptSemantics.Epoch;
        var count = 0;
        foreach (var authored in documents.AuthoredDocuments)
            if (authored is ScriptSourceDocument source && source.IsCSharpSource)
            {
                count++;
                if (!observedScriptVersions.TryGetValue(source, out var version) || version != source.Version) changed = true;
            }
        changed |= count != observedScriptVersions.Count;
        if (changed)
        {
            observedScriptVersions.Clear();
            foreach (var authored in documents.AuthoredDocuments)
                if (authored is ScriptSourceDocument source && source.IsCSharpSource) observedScriptVersions[source] = source.Version;
            semanticTarget = target; observedSemanticEpoch = scriptSemantics.Epoch;
            scriptSemanticGeneration++; semanticChangedAt = Stopwatch.GetTimestamp(); semanticPending = target != null;
            scriptSemanticResult = null; scriptSemanticCancellation?.Cancel();
            if (target != null && scriptEditors.TryGetValue(target, out var editor)) editor.InvalidateSemanticResult(target.EditorText, contextChanged);
        }
        if (scriptSemanticTask?.IsCompleted == true)
        {
            var completed = scriptSemanticTask; scriptSemanticTask = null;
            scriptSemanticCancellation?.Dispose(); scriptSemanticCancellation = null;
            try
            {
                var result = completed.GetAwaiter().GetResult();
                if (runningSemanticGeneration == scriptSemanticGeneration && target != null && target.Version == result.Source.Version)
                { scriptSemanticResult = result; ReportScriptDiagnostics(result.Diagnostics); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (runningSemanticGeneration == scriptSemanticGeneration)
                    outputLog.Append("Project", LogMessageType.Warning, "ScriptWorkspace", error.Message);
            }
        }
        if (!semanticPending || target == null || scriptSemanticTask != null || !scriptSemantics.IsSettled
            || Stopwatch.GetElapsedTime(semanticChangedAt).TotalMilliseconds < 300) return;
        var snapshots = CaptureScriptSourceSnapshots();
        var service = scriptSemantics; var path = target.Path;
        scriptSemanticCancellation = new CancellationTokenSource(); var cancellation = scriptSemanticCancellation.Token;
        runningSemanticGeneration = scriptSemanticGeneration; semanticPending = false;
        scriptSemanticTask = Task.Run(() => service.Analyze(snapshots, path, cancellation), cancellation);
    }

    private void StopScriptSemantics()
    {
        StopScriptComponentSources();
        StopVisualScriptCompilation();
        StopScriptCodeAssistance();
        reportedScriptDiagnostics.Clear();
        scriptSemanticCancellation?.Cancel();
        var task = scriptSemanticTask; var cancellation = scriptSemanticCancellation;
        if (task != null) _ = task.ContinueWith(completed => { _ = completed.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
        else cancellation?.Dispose();
        scriptSemanticTask = null; scriptSemanticCancellation = null;
        scriptSemantics?.Dispose(); scriptSemantics = null;
        semanticProjectWorkspace = null; semanticProjectPath = semanticConfiguration = semanticFramework = ""; semanticTarget = null;
        observedScriptVersions.Clear(); scriptSemanticResult = null; observedSemanticEpoch = -1;
        scriptProjectSnapshots.Clear(); scriptProjectSnapshotError = null;
        scriptSemanticGeneration++; semanticPending = false;
    }
}
