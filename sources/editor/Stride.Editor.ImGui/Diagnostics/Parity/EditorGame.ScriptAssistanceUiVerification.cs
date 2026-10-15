using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal const string CompletionUiText = "namespace EditorParity {\n internal class CompletionUiCoverage {\n int CoverageWidgetCompletion = 41;\n int Compute() { return Cove; }\n }\n}\n";
    internal const string CompletionUiCommittedText = "namespace EditorParity {\n internal class CompletionUiCoverage {\n int CoverageWidgetCompletion = 41;\n int Compute() { return CoverageWidgetCompletion; }\n }\n}\n";
    private ScriptSourceDocument? completionUiDocument;
    private EditorAuthoringState? completionUiOwner;
    private string? completionUiOriginalText;
    private string? completionUiExpectedCommittedNativeText;
    private byte[]? completionUiOriginalBytes;
    private int completionUiOriginalPosition;
    private Guid[] completionUiOriginalJournalIds = [];
    private Guid completionUiArrangementId;
    private Guid? completionUiCommitId;
    private Task? completionUiDrain;
    private object? completionUiLastControl, completionUiAcceptedControl;
    private int completionUiAcceptedFrame = -1;
    private string? completionUiAcceptedPath;
    internal string PrepareScriptCompletionUiVerification()
    {
        if (completionUiDocument != null || stockDispatchSourcePath == null || sessionHistory == null)
            throw new InvalidOperationException("Completion UI requires the retained stock source and clean owned fixture.");
        var owner = authoring?.Find(stockDispatchSourcePath);
        if (owner?.Document is not ScriptSourceDocument document || document.IsDirty || !document.CanEditText)
            throw new InvalidOperationException("Completion UI source is unavailable or dirty.");
        completionUiDocument = document; completionUiOwner = owner;
        completionUiOriginalText = document.Text; completionUiOriginalBytes = File.ReadAllBytes(document.Path);
        completionUiOriginalPosition = sessionHistory.History.Position;
        completionUiOriginalJournalIds = sessionHistory.History.Entries.Where(entry => entry.IsDone).Select(entry => entry.Id).ToArray();
        var entry = documents.Find(document.Path) ?? throw new InvalidOperationException("Completion UI lost its actual document entry.");
        ActivateDocument(entry); document.UpdateEditorText(CompletionUiText);
        // The native source owns its original newline convention even when the
        // editor buffer uses LF. Capture that convention before the tested commit.
        completionUiExpectedCommittedNativeText = document.Text.Replace("Cove;", "CoverageWidgetCompletion;", StringComparison.Ordinal);
        if (sessionHistory.History.Position != completionUiOriginalPosition + 1)
            throw new InvalidOperationException("Completion UI text arrangement must own exactly one native source journal.");
        completionUiArrangementId = sessionHistory.History.Entries.Last(item => item.IsDone).Id;
        return document.Path;
    }
    internal bool PrepareScriptCompletionUiCaret()
    {
        if (completionUiDocument is not { } document || !scriptEditors.TryGetValue(document, out var editor)) return false;
        editor.NavigatePosition(CompletionUiText.IndexOf("Cove;", StringComparison.Ordinal) + 4, document.EditorText);
        return true;
    }
    internal bool ScriptCompletionUiReady => completionUiDocument is { } document
        && ReferenceEquals(CommandDocument, document) && scriptEditors.TryGetValue(document, out var editor)
        && editor.InputActive && editor.CaretPosition == CompletionUiText.IndexOf("Cove;", StringComparison.Ordinal) + 4
        && !IsAuthoringPipelineUpdating && !RecoveryTransitionBusy;
    internal bool ScriptCompletionUiAccepted(int sinceFrame) => completionUiAcceptedFrame >= sinceFrame
        && completionUiDocument != null && completionUiAcceptedPath == completionUiDocument.Path;
    private unsafe void RecordScriptAssistanceUiControl(ScriptSourceDocument document, string id, bool pressed)
    {
        Controls.Record(id);
        var context = Gui.GetCurrentContext();
        completionUiLastControl = new { id, frame = Gui.GetFrameCount(), nativeId = context.LastItemData.ID,
            pressed, hovered = Gui.IsItemHovered(), active = Gui.IsItemActive(), document.Path, document.Version,
            nativeDown = Gui.IsMouseDown(ImGuiMouseButton.Left), nativeReleased = Gui.IsMouseReleased(ImGuiMouseButton.Left) };
        if (pressed && id == "script/completion/CoverageWidgetCompletion" && ReferenceEquals(document, completionUiDocument))
        {
            completionUiAcceptedFrame = Gui.GetFrameCount(); completionUiAcceptedPath = document.Path;
            completionUiAcceptedControl = completionUiLastControl;
        }
    }
    internal object CaptureScriptCompletionUiEvidence() => new
    {
        path = completionUiDocument?.Path, originalHistoryPosition = completionUiOriginalPosition,
        originalJournalIds = completionUiOriginalJournalIds, arrangementJournalId = completionUiArrangementId,
        completionJournalId = completionUiCommitId,
        historyPosition = sessionHistory?.History.Position, originalBytesUnchanged = completionUiDocument != null
            && File.ReadAllBytes(completionUiDocument.Path).SequenceEqual(completionUiOriginalBytes!),
        text = completionUiDocument?.EditorText, dirty = completionUiDocument?.IsDirty,
        nativeText = completionUiDocument?.Text, diskText = completionUiDocument == null ? null : File.ReadAllText(completionUiDocument.Path),
        expectedCommittedNativeText = completionUiExpectedCommittedNativeText,
        saveReceipt = new { AssetTemplateSaveUiReceipt.Accepted, AssetTemplateSaveUiReceipt.Eligible, AssetTemplateSaveUiReceipt.Frame },
        IsPackageMetadataSaving, CanUndoCurrent, CanRedoCurrent, commandPath = CommandDocument?.Path,
        committedSavedPredicate = ScriptCompletionUiSaved(CompletionUiCommittedText),
        undoReadyPredicate = completionUiDocument != null && StockHistoryToolbarReady("undo", completionUiDocument.Path),
        nativeHistoryToolbar = new { current = stockHistoryToolbarCurrent, lastPressed = stockHistoryToolbarPressed,
            readiness = stockHistoryToolbarReadiness.Select(item => new { action = item.Key, item.Value.Frame,
                item.Value.NativeId, item.Value.Enabled, item.Value.Path }).ToArray(),
            liveGuards = InspectStockHistoryToolbarGuards() },
        canonical = completionUiDocument != null && ReferenceEquals(authoring?.Find(completionUiDocument.Path), completionUiOwner),
        nativeEditor = completionUiDocument != null && scriptEditors.TryGetValue(completionUiDocument, out var editor)
            ? editor.CaptureAssistanceUiEvidence() : null,
        lastControl = completionUiLastControl, acceptedControl = completionUiAcceptedControl,
        assistanceTaskPending = scriptAssistTask is { IsCompleted: false }, requestKind = assistingRequestKind.ToString()
    };
    internal bool ScriptCompletionUiState(string text, int journals, bool originalBytes)
    {
        if (completionUiDocument is not { } document || !ReferenceEquals(authoring?.Find(document.Path), completionUiOwner)
            || document.EditorText != text || sessionHistory?.History.Position != completionUiOriginalPosition + journals
            || originalBytes && !File.ReadAllBytes(document.Path).SequenceEqual(completionUiOriginalBytes!)) return false;
        var applied = sessionHistory.History.Entries.Where(entry => entry.IsDone).Select(entry => entry.Id).ToArray();
        if (!applied.Take(completionUiOriginalPosition).SequenceEqual(completionUiOriginalJournalIds)
            || journals > 0 && applied[completionUiOriginalPosition] != completionUiArrangementId) return false;
        foreach (var id in applied.Skip(completionUiOriginalPosition))
        {
            var transaction = sessionHistory.Stack.RetrieveAllTransactions().Single(entry => entry.Id == id);
            if (sessionHistory.GetParticipants(transaction).Any(owner => !ReferenceEquals(owner, completionUiOwner))) return false;
        }
        if (journals == 2)
        {
            var id = applied[^1];
            if (completionUiCommitId != null && completionUiCommitId != id) return false;
            completionUiCommitId = id;
        }
        return true;
    }
    internal bool ScriptCompletionUiSaved(string text)
    {
        var expectedNativeText = text == CompletionUiCommittedText ? completionUiExpectedCommittedNativeText : completionUiOriginalText;
        return completionUiDocument is { IsDirty: false } document && expectedNativeText != null
            && document.EditorText == text && document.Text == expectedNativeText
            && File.ReadAllText(document.Path) == expectedNativeText && !IsPackageMetadataSaving;
    }
    internal void CompleteScriptCompletionUiVerification()
    {
        if (!ScriptCompletionUiState(completionUiOriginalText!, 0, true) || completionUiDocument!.IsDirty)
            throw new InvalidOperationException("Completion UI did not restore exact source/history/saved baseline.");
        completionUiDocument = null; completionUiOwner = null;
    }
    internal string ScriptCompletionUiOriginalText => completionUiOriginalText!;
    internal void CancelScriptCompletionUiVerification()
    {
        var task = scriptAssistTask; StopScriptAssistanceRequest();
        completionUiDrain = task ?? Task.CompletedTask;
        QueueShellParityAsyncCleanup(completionUiDrain, null);
    }
    internal bool TryDrainScriptCompletionUiVerification()
    {
        if (!TryDrainScriptAssistanceRetirements()) return false;
        if (completionUiDrain is { IsCompleted: false }) return false;
        if (completionUiDrain?.IsFaulted == true) _ = completionUiDrain.Exception;
        if (completionUiDocument is { } document)
        {
            if (!ReferenceEquals(authoring?.Find(document.Path), completionUiOwner) || sessionHistory == null)
                throw new InvalidOperationException("Completion UI cleanup lost its canonical owner/history.");
            while (sessionHistory.History.Position > completionUiOriginalPosition)
            {
                var applied = sessionHistory.History.Entries.Last(entry => entry.IsDone);
                var transaction = sessionHistory.Stack.RetrieveAllTransactions().Single(entry => entry.Id == applied.Id);
                if (sessionHistory.GetParticipants(transaction).Any(owner => !ReferenceEquals(owner, completionUiOwner)))
                    throw new InvalidOperationException("Completion UI cleanup refused a foreign authored journal.");
                sessionHistory.Undo();
            }
            if (!ScriptCompletionUiState(completionUiOriginalText!, 0, false))
                throw new InvalidOperationException("Completion UI cleanup did not restore its exact source/history prefix.");
            if (document.IsDirty || !File.ReadAllBytes(document.Path).SequenceEqual(completionUiOriginalBytes!)) document.Save();
            CompleteScriptCompletionUiVerification();
        }
        completionUiDrain = null; return true;
    }
}
