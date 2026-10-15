using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Task<VisualScriptSemanticResult>? visualScriptCompilation;
    private CancellationTokenSource? visualScriptCancellation;
    private VisualScriptDocument? visualScriptTarget;
    private long visualScriptRevision = -1, visualScriptEpoch = -1, visualScriptSourceGeneration = -1, visualScriptChangedAt;
    private bool visualScriptPending;

    private void PumpVisualScriptCompilation()
    {
        var target = documents.Active?.Document as VisualScriptDocument;
        if (scriptSemantics == null || target == null)
        {
            if (visualScriptTarget != null) StopVisualScriptCompilation();
            return;
        }
        var changed = !ReferenceEquals(visualScriptTarget, target) || target.EditRevision != visualScriptRevision
            || visualScriptEpoch != scriptSemantics.Epoch || visualScriptSourceGeneration != scriptSemanticGeneration;
        if (changed)
        {
            visualScriptTarget = target; visualScriptRevision = target.EditRevision; visualScriptEpoch = scriptSemantics.Epoch;
            visualScriptSourceGeneration = scriptSemanticGeneration; visualScriptChangedAt = Stopwatch.GetTimestamp();
            visualScriptPending = true; visualScriptCancellation?.Cancel();
            if (visualScriptViews.TryGetValue(target, out var view)) view.Semantics = null;
        }
        if (visualScriptCompilation?.IsCompleted == true)
        {
            var completed = visualScriptCompilation; visualScriptCompilation = null;
            visualScriptCancellation?.Dispose(); visualScriptCancellation = null;
            try
            {
                var result = completed.GetAwaiter().GetResult();
                if (!visualScriptPending && ReferenceEquals(target, visualScriptTarget) && target.EditRevision == result.Revision
                    && result.Epoch == scriptSemantics.Epoch && visualScriptSourceGeneration == scriptSemanticGeneration)
                {
                    if (!visualScriptViews.TryGetValue(target, out var view)) visualScriptViews.Add(target, view = new());
                    view.Semantics = result;
                    if (target.CanSave)
                        Execute(() =>
                        {
                            using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
                            {
                                foreach (var generated in result.Slots)
                                {
                                    var method = target.Asset.Methods.FirstOrDefault(method => method.Id == generated.Method);
                                    var block = method?.Blocks.Values.FirstOrDefault(block => block.Id == generated.Block);
                                    if (method != null && block != null) target.ApplyGeneratedSlots(method, block, generated.Slots);
                                }
                            });
                            if (capture.Entries.Count != 0) capture.Publish(sessionHistory, "Updated slots");
                        });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { outputLog.Append("Project", LogMessageType.Warning, "VisualScript", error.Message); }
        }
        if (!visualScriptPending || target.HasPendingVisualTextEdits || visualScriptCompilation != null || Stopwatch.GetElapsedTime(visualScriptChangedAt).TotalMilliseconds < 300
            || target.IsDirty && !target.CanSave || sessionHistory?.Stack.TransactionInProgress == true || sessionHistory?.Stack.RollInProgress == true) return;
        try
        {
            var owner = GetVisualScriptCompilerOwner(target);
            var snapshot = target.CaptureCompilation(owner.Item);
            var sources = CaptureScriptSourceSnapshots();
            visualScriptCancellation = new(); visualScriptPending = false;
            var service = scriptSemantics; var token = visualScriptCancellation.Token;
            visualScriptCompilation = Task.Run(() => service.AnalyzeVisualScript(snapshot, sources, token), token);
        }
        catch (Exception error)
        { visualScriptPending = false; outputLog.Append("Project", LogMessageType.Warning, "VisualScript", error.Message); }
    }
    private void StopVisualScriptCompilation()
    {
        visualScriptCancellation?.Cancel();
        var cancellation = visualScriptCancellation;
        if (visualScriptCompilation is { } task)
            _ = task.ContinueWith(completed => { _ = completed.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
        else cancellation?.Dispose();
        visualScriptCompilation = null; visualScriptCancellation = null;
        visualScriptTarget = null; visualScriptPending = false; visualScriptRevision = visualScriptEpoch = visualScriptSourceGeneration = -1;
    }
    private Compilation? GetVisualScriptCompilation(VisualScriptDocument document)
        => visualScriptViews.TryGetValue(document, out var view) && view.Semantics is { } result
            && result.Revision == document.EditRevision && result.Epoch == scriptSemantics?.Epoch ? result.Compilation : null;
}
