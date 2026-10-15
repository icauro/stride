using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Task<ScriptAssistResult>? scriptAssistTask;
    private CancellationTokenSource? scriptAssistCancellation;
    private ScriptSemanticWorkspace? assistingWorkspace;
    private ScriptSourceDocument? assistingDocument;
    private ScriptTextEditor? assistingEditor;
    private ScriptSourceSnapshot[] assistingSnapshots = [];
    private ScriptAssistResult? pendingScriptAssistance;
    private ScriptDefinition? pendingScriptDefinition;
    private long assistingEpoch;
    private long assistingRequestGeneration;
    private ScriptAssistKind assistingRequestKind;
    private readonly Queue<string> pendingScriptSourceOpenPaths = new();

    private void PumpScriptCodeAssistance()
    {
        TryDrainScriptAssistanceRetirements();
        while (pendingScriptSourceOpenPaths.TryDequeue(out var sourcePath)) Execute(() => OpenScriptDocument(sourcePath));
        if (pendingScriptDefinition is { } definition)
        {
            pendingScriptDefinition = null;
            Execute(() => NavigateScriptDiagnostic(new BuildDiagnostic(definition.Path, definition.Line, definition.Column, LogMessageType.Info, "", "")));
        }
        if (scriptAssistTask == null) return;
        var valid = ReferenceEquals(assistingWorkspace, scriptSemantics) && assistingWorkspace?.Epoch == assistingEpoch
            && assistingEditor?.AssistanceGeneration == assistingRequestGeneration && ReferenceEquals(documents.Active?.Document, assistingDocument)
            && documents.AuthoredDocuments.OfType<ScriptSourceDocument>().Count(source => source.IsCSharpSource) == assistingSnapshots.Length
            && assistingSnapshots.All(snapshot => FindAuthoredScript(snapshot.Path) is { } source && source.Version == snapshot.Version && source.EditorText == snapshot.Text);
        if (!valid) scriptAssistCancellation?.Cancel();
        if (!scriptAssistTask.IsCompleted) return;
        var task = scriptAssistTask; scriptAssistTask = null;
        scriptAssistCancellation?.Dispose(); scriptAssistCancellation = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            if (valid) { pendingScriptAssistance?.CodeActionBatch?.Dispose(); pendingScriptAssistance = result; }
            else { result.CodeActionBatch?.Dispose(); if (assistingRequestKind == ScriptAssistKind.DiscoverCodeActions) assistingEditor?.RetryCodeActionDiscovery(); }
        }
        catch (OperationCanceledException) { if (assistingRequestKind == ScriptAssistKind.DiscoverCodeActions) assistingEditor?.RetryCodeActionDiscovery(); }
        catch (Exception error) { if (assistingRequestKind == ScriptAssistKind.DiscoverCodeActions) assistingEditor?.SuppressCodeActionDiscovery(); if (valid) outputLog.Append("Project", LogMessageType.Warning, "ScriptWorkspace", error.Message); }
    }
    private void HandleScriptCodeAssistance(ScriptSourceDocument document, ScriptTextEditor editor)
    {
        if (editor.TakeConfirmedCodeAction() is { } confirmed)
            Execute(() => { try { editor.ApplyConfirmedCodeAction(confirmed, document, ApplyScriptCodeAction); } finally { confirmed.CodeActionBatch?.Dispose(); } });
        if (pendingScriptAssistance is { } result && ReferenceEquals(document, assistingDocument) && ReferenceEquals(editor, assistingEditor))
        {
            pendingScriptAssistance = null;
            if (result.Failures is { } failures)
                foreach (var failure in failures) outputLog.Append("Project", LogMessageType.Warning, "Code action", failure.Message);
            Execute(() => editor.ApplyAssistance(result, document));
        }
        if (editor.TakeDefinition() is { } definition) pendingScriptDefinition = definition;
        var request = editor.TakeAssistanceRequest();
        if (request == null || scriptSemantics == null) return;
        StopScriptAssistanceRequest();
        assistingWorkspace = scriptSemantics; assistingDocument = document; assistingEditor = editor;
        assistingEpoch = scriptSemantics.Epoch;
        assistingRequestGeneration = request.Generation;
        assistingRequestKind = request.Kind;
        assistingSnapshots = CaptureScriptSourceSnapshots();
        scriptAssistCancellation = new CancellationTokenSource(); var cancellation = scriptAssistCancellation.Token;
        var service = assistingWorkspace; var snapshots = assistingSnapshots;
        scriptAssistTask = Task.Run(() => service.Assist(snapshots, document.Path, request, cancellation), cancellation);
    }
    private void StopScriptAssistanceRequest()
    {
        var task = scriptAssistTask; var cancellation = scriptAssistCancellation;
        if (task != null) scriptAssistanceRetirements.Add(EditorBackgroundJobRetirement.Retire(task, cancellation,
            result => result.CodeActionBatch?.Dispose(), "Script assistance"));
        else if (cancellation != null) scriptAssistanceRetirements.Add(EditorBackgroundJobRetirement.Retire(
            Task.FromResult<object?>(null), cancellation, static _ => { }, "Script assistance token"));
        if (pendingScriptAssistance is { } pending) scriptAssistanceRetirements.Add(EditorBackgroundJobRetirement.Retire(
            Task.FromResult(pending), null, result => result.CodeActionBatch?.Dispose(), "Unadopted script assistance"));
        scriptAssistTask = null; scriptAssistCancellation = null; pendingScriptAssistance = null;
        assistingDocument = null; assistingEditor = null; assistingWorkspace = null; assistingSnapshots = [];
    }
    private readonly List<Task> scriptAssistanceRetirements = [];
    internal bool TryDrainScriptAssistanceRetirements()
    {
        for (var index = scriptAssistanceRetirements.Count - 1; index >= 0; index--)
        {
            var task = scriptAssistanceRetirements[index];
            if (!task.IsCompleted) continue;
            task.GetAwaiter().GetResult(); scriptAssistanceRetirements.RemoveAt(index);
        }
        return scriptAssistanceRetirements.Count == 0;
    }
    private void StopScriptCodeAssistance()
    {
        pendingScriptSourceOpenPaths.Clear();
        StopScriptAssistanceRequest(); pendingScriptDefinition = null;
        foreach (var editor in scriptEditors.Values) editor.DismissAssistance();
    }
}
