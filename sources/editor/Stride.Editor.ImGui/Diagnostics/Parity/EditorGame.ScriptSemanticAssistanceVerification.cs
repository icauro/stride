using Microsoft.CodeAnalysis;
using System.Text.Json;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IEnumerator<object?>? scriptSemanticVerification;
    private CancellationTokenSource? scriptSemanticVerificationCancellation;
    private readonly List<Task> scriptSemanticVerificationTasks = [];
    private Task? scriptSemanticVerificationDrain;
    private ScriptSourceDocument? scriptSemanticCleanupDocument;
    private EditorAuthoringState? scriptSemanticCleanupOwner;
    private EditorSessionHistory? scriptSemanticCleanupHistory;
    private int scriptSemanticCleanupPosition;
    private string? scriptSemanticCleanupText;
    private byte[]? scriptSemanticCleanupBytes;
    internal void StartScriptSemanticAssistanceVerification(string artifacts)
    {
        if (scriptSemanticVerification != null || scriptSemanticVerificationDrain != null || scriptSemanticCleanupDocument != null)
            throw new InvalidOperationException("Semantic verification is already pending or retiring.");
        scriptSemanticVerificationCancellation = new();
        scriptSemanticVerification = RunScriptSemanticAssistanceVerification(artifacts, scriptSemanticVerificationCancellation.Token).GetEnumerator();
    }
    internal object? StepScriptSemanticAssistanceVerification()
    {
        var run = scriptSemanticVerification ?? throw new InvalidOperationException("Semantic verification is not pending.");
        if (!run.MoveNext()) throw new InvalidOperationException("Semantic verification ended without evidence.");
        var evidence = run.Current;
        if (evidence != null)
        {
            run.Dispose(); scriptSemanticVerification = null;
            ClearSemanticCleanupBaseline(); RetireSemanticTasks();
        }
        return evidence;
    }
    internal void CancelScriptSemanticAssistanceVerification()
    {
        scriptSemanticVerificationCancellation?.Cancel(); scriptSemanticVerification?.Dispose(); scriptSemanticVerification = null;
        RetireSemanticTasks();
    }
    private T TrackSemanticTask<T>(T task) where T : Task { scriptSemanticVerificationTasks.Add(task); return task; }
    private void RetireSemanticTasks()
    {
        if (scriptSemanticVerificationDrain != null) return;
        var cancellation = scriptSemanticVerificationCancellation; scriptSemanticVerificationCancellation = null;
        scriptSemanticVerificationDrain = Task.WhenAll(scriptSemanticVerificationTasks);
        scriptSemanticVerificationTasks.Clear();
        QueueShellParityAsyncCleanup(scriptSemanticVerificationDrain, cancellation);
    }
    internal bool TryDrainScriptSemanticAssistanceVerification()
    {
        if (scriptSemanticVerificationDrain is { IsCompleted: false }) return false;
        if (scriptSemanticVerificationDrain?.IsFaulted == true) _ = scriptSemanticVerificationDrain.Exception;
        if (scriptSemanticCleanupDocument is { } document)
        {
            var history = scriptSemanticCleanupHistory!;
            if (!ReferenceEquals(sessionHistory, history) || !ReferenceEquals(authoring?.Find(document.Path), scriptSemanticCleanupOwner)
                || !ReferenceEquals(scriptSemanticCleanupOwner!.Document, document))
                throw new InvalidOperationException("Semantic cleanup lost its actual canonical source/history owner.");
            while (history.History.Position > scriptSemanticCleanupPosition)
            {
                var applied = history.History.Entries.Last(entry => entry.IsDone);
                var transaction = history.Stack.RetrieveAllTransactions().Single(entry => entry.Id == applied.Id);
                if (history.GetParticipants(transaction).Any(owner => !ReferenceEquals(owner, scriptSemanticCleanupOwner)))
                    throw new InvalidOperationException("Semantic cleanup refused to roll back another authored participant.");
                history.Undo();
            }
            if (history.History.Position != scriptSemanticCleanupPosition || document.Text != scriptSemanticCleanupText)
                throw new InvalidOperationException("Semantic cleanup did not restore the actual native history prefix.");
            if (document.IsDirty || !File.ReadAllBytes(document.Path).SequenceEqual(scriptSemanticCleanupBytes!)) document.Save();
            if (!File.ReadAllBytes(document.Path).SequenceEqual(scriptSemanticCleanupBytes!))
                throw new InvalidOperationException("Semantic cleanup did not restore exact canonical saved bytes.");
            ClearSemanticCleanupBaseline();
        }
        scriptSemanticVerificationDrain = null;
        return true;
    }
    private void ClearSemanticCleanupBaseline()
    {
        scriptSemanticCleanupDocument = null; scriptSemanticCleanupOwner = null; scriptSemanticCleanupHistory = null;
        scriptSemanticCleanupText = null; scriptSemanticCleanupBytes = null;
    }
    private IEnumerable<object?> RunScriptSemanticAssistanceVerification(string artifacts, CancellationToken cancellation)
    {
        if (!args.Contains("--ui-test") || workspace == null || sessionHistory == null || stockDispatchSourcePath == null)
            throw new InvalidOperationException("Semantic verification requires the previously dispatched real private source fixture.");
        var state = authoring?.Find(stockDispatchSourcePath)
            ?? throw new InvalidOperationException("Stock source canonical owner is unavailable.");
        if (state.Document is not ScriptSourceDocument document || document.ProjectPath == null || !document.CanEditText || document.IsDirty)
            throw new InvalidOperationException("The native included source owner must be writable and clean.");
        var previous = documents.Active;
        var entry = documents.Find(document) ?? documents.Add(document); ActivateDocument(entry);
        while (scriptSemantics == null || !SamePath(semanticProjectPath, document.ProjectPath)) { cancellation.ThrowIfCancellationRequested(); yield return null; }
        var service = scriptSemantics;
        Directory.CreateDirectory(artifacts);
        var baselineText = document.Text; var baselineBytes = File.ReadAllBytes(document.Path);
        var historyPosition = sessionHistory.History.Position;
        scriptSemanticCleanupDocument = document; scriptSemanticCleanupOwner = state; scriptSemanticCleanupHistory = sessionHistory;
        scriptSemanticCleanupPosition = historyPosition; scriptSemanticCleanupText = baselineText; scriptSemanticCleanupBytes = baselineBytes;
        const string fixtureText = "namespace EditorParity { internal class SemanticCoverage { int CoverageMarker = 17; int ComputeCoverage(int left, int right) => left + right; int Compute() { return Cove + ComputeCoverage(1, 2); } } }";
        document.UpdateEditorText(fixtureText);
        yield return null;
        Check(sessionHistory.History.Position == historyPosition + 1 && document.IsDirty && baselineBytes.SequenceEqual(File.ReadAllBytes(document.Path)),
            "Unsaved semantic fixture uses exactly one canonical source journal without modifying disk");
        var completionPosition = fixtureText.IndexOf("Cove +", StringComparison.Ordinal) + "Cove".Length;
        var snapshots = CaptureScriptSourceSnapshots();
        var analyze = TrackSemanticTask(Task.Run(() => service.Analyze(snapshots, document.Path, cancellation), cancellation));
        while (!analyze.IsCompleted) { cancellation.ThrowIfCancellationRequested(); yield return null; }
        ScriptSemanticResult diagnostic;
        try { diagnostic = analyze.GetAwaiter().GetResult(); }
        finally
        {
            WriteReceipt("design-time-workspace", service.DesignTimeLoadEvidence);
            WriteReceipt("native-composition", service.NativeCompositionEvidence);
        }
        WriteReceipt("diagnostics", new { diagnostic.Source, diagnostic.Issues });
        Check(diagnostic.Source.Version == document.Version && diagnostic.Source.Text == document.EditorText
            && diagnostic.Issues.Any(issue => issue.Severity == DiagnosticSeverity.Error && issue.Message.Contains("Cove", StringComparison.Ordinal)),
            "Real Roslyn diagnostics must report the distinguishing undeclared identifier from the current unsaved source");
        var completionTask = TrackSemanticTask(Task.Run(() => service.Assist(snapshots, document.Path,
            new ScriptAssistRequest(ScriptAssistKind.Completion, completionPosition, document.Version), cancellation), cancellation));
        while (!completionTask.IsCompleted) { cancellation.ThrowIfCancellationRequested(); yield return null; }
        var completion = completionTask.GetAwaiter().GetResult().Completion ?? throw new InvalidOperationException("Native Roslyn completion returned no set.");
        var item = Array.FindIndex(completion.Items, value => value.DisplayText == "CoverageMarker");
        WriteReceipt("completion", new { completion.Source, completion.Epoch, completion.Position,
            items = completion.Items.Select(value => value.DisplayText).ToArray(), selectedItem = item });
        Check(item >= 0 && completion.Source.Text == document.EditorText, "Actual completion provider must resolve a member declared only in retained unsaved text");
        var commitTask = TrackSemanticTask(Task.Run(() => service.Assist(snapshots, document.Path,
            new ScriptAssistRequest(ScriptAssistKind.CommitCompletion, completionPosition, document.Version, completion, item), cancellation), cancellation));
        while (!commitTask.IsCompleted) { cancellation.ThrowIfCancellationRequested(); yield return null; }
        var commit = commitTask.GetAwaiter().GetResult();
        WriteReceipt("commit", new { commit.Source, commit.Changes, commit.NewPosition,
            currentVersion = document.Version, canonicalGeneration = state.Generation });
        Check(commit.Changes is { Length: > 0 }, "Native completion provider must supply actual source changes");
        document.UpdateEditorChanges(commit.Changes!); yield return null;
        var completedText = document.Text;
        Check(completedText.Contains("return CoverageMarker +", StringComparison.Ordinal) && sessionHistory.History.Position == historyPosition + 2
            && ReferenceEquals(authoring.Find(document.Path), state), "Applying provider changes creates one canonical journal with exact retained owner");
        sessionHistory.Undo(); yield return null;
        Check(document.Text == fixtureText, "Completion Undo restores the exact unsaved draft");
        sessionHistory.Redo(); yield return null;
        Check(document.Text == completedText, "Completion Redo restores exact provider edit without additional history");
        snapshots = CaptureScriptSourceSnapshots();
        var signaturePosition = document.EditorText.IndexOf("ComputeCoverage(1, ", StringComparison.Ordinal) + "ComputeCoverage(1, ".Length;
        var signatureTask = TrackSemanticTask(Task.Run(() => service.Assist(snapshots, document.Path,
            new ScriptAssistRequest(ScriptAssistKind.Signature, signaturePosition, document.Version), cancellation), cancellation));
        while (!signatureTask.IsCompleted) { cancellation.ThrowIfCancellationRequested(); yield return null; }
        var signatures = signatureTask.GetAwaiter().GetResult().Signatures;
        WriteReceipt("signatures", new { document.Version, signatures, signaturePosition });
        Check(signatures?.Any(signature => signature.Parts.Any(part => part.Active && part.Text.Contains("right", StringComparison.Ordinal))) == true,
            "Actual native signature providers identify the active second parameter from retained authored context");
        var definitionPosition = document.EditorText.IndexOf("return CoverageMarker", StringComparison.Ordinal) + "return ".Length + 2;
        var definitionTask = TrackSemanticTask(Task.Run(() => service.Assist(snapshots, document.Path,
            new ScriptAssistRequest(ScriptAssistKind.Definition, definitionPosition, document.Version), cancellation), cancellation));
        while (!definitionTask.IsCompleted) { cancellation.ThrowIfCancellationRequested(); yield return null; }
        var definitions = definitionTask.GetAwaiter().GetResult().Definitions;
        WriteReceipt("definitions", new { document.Version, definitions, definitionPosition });
        Check(definitions?.Any(definition => SamePath(definition.Path, document.Path)) == true,
            "Actual symbol definition maps the unsaved member back to its canonical authored source path");
        var staleTask = TrackSemanticTask(Task.Run(() => service.Assist(snapshots, document.Path,
            new ScriptAssistRequest(ScriptAssistKind.CommitCompletion, completionPosition, document.Version, completion, item), cancellation), cancellation));
        while (!staleTask.IsCompleted) { cancellation.ThrowIfCancellationRequested(); yield return null; }
        try { staleTask.GetAwaiter().GetResult(); throw new InvalidOperationException("Stale completion was accepted."); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        Check(document.Text == completedText && sessionHistory.History.Position == historyPosition + 2, "Stale provider changes are rejected without authored mutation");
        document.Save(); yield return null;
        Check(File.ReadAllText(document.Path) == completedText && !document.IsDirty, "Native source Save persists exact completed text");
        sessionHistory.Undo(); yield return null;
        sessionHistory.Undo(); yield return null;
        Check(document.Text == baselineText && sessionHistory.History.Position == historyPosition, "Native history restores the original source and applied prefix");
        document.Save(); yield return null;
        Check(baselineBytes.SequenceEqual(File.ReadAllBytes(document.Path)) && !document.IsDirty, "Semantic cleanup restores exact original saved source bytes");
        if (previous != null && documents.Entries.Contains(previous)) ActivateDocument(previous);
        var result = new { scenario = "scripts.semantic-assistance", passed = true,
            completion = completion.Items[item].DisplayText, diagnosticCount = diagnostic.Issues.Length,
            signatures = signatures!.Select(signature => string.Concat(signature.Parts.Select(part => part.Text))).ToArray(), definitions,
            nativeProviderCommitUndoRedo = true, staleCompletionRejected = true, originalBytesRestored = true,
            coverage = "Actual shared MSBuild/Roslyn workspace providers over retained unsaved canonical source, diagnostic/completion/signature/definition services, provider changes through source history and native persistence. No completion popup/keyboard gesture, metadata-as-source or code-action acceptance claim." };
        Directory.CreateDirectory(artifacts); File.WriteAllText(Path.Combine(artifacts, "semantic-assistance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        yield return result;
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void WriteReceipt(string stage, object value) => File.WriteAllText(Path.Combine(artifacts, stage + ".json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
