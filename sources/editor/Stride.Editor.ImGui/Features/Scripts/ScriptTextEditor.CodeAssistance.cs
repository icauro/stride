using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Completion;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private int caretByte, caretPosition, priorCaretByte;
    private int selectionStartByte, selectionEndByte, priorSelectionStartByte, priorSelectionEndByte;
    private TextSpan selection;
    private ScriptAssistRequest? assistRequest;
    private ScriptCompletionSet? completion;
    private ScriptSignature[] signatures = [];
    private ScriptDefinition[] definitions = [];
    private ScriptCodeActionItem[] codeActions = [];
    private int selectedCompletion, selectedSignature;
    private bool commitOnEnter, preserveCompletionCursor;
    private Vector2 assistancePosition;
    private int[] lineStarts = [0];
    private int assistanceCaret = -1;
    private float assistanceFont = -1, assistanceCaretX;
    private bool completionPending;
    private bool codeActionShortcut;
    private ScriptAssistRequest? typedCompletionCommit;
    public long AssistanceGeneration { get; private set; }
    public int CaretPosition => caretPosition;
    public ScriptDefinition? SelectedDefinition { get; private set; }
    public ScriptAssistRequest? TakeAssistanceRequest() { var value = assistRequest; assistRequest = null; return value == null ? null : value with { Generation = ++AssistanceGeneration }; }
    public void DismissAssistance() { completion = null; signatures = []; definitions = []; codeActions = []; assistRequest = null; completionPending = false; ClearCodeActionPreview(); RetryCodeActionDiscovery(); AssistanceGeneration++; }
    public void NavigatePosition(int position, string text)
    {
        var point = SourceText.From(text).Lines.GetLinePosition(Math.Clamp(position, 0, text.Length));
        Navigate(point.Line + 1, point.Character + 1);
    }

    private bool PrepareAssistanceInput(ScriptSourceDocument document, ref ImGuiInputTextFlags flags)
    {
        priorCaretByte = caretByte; commitOnEnter = false; preserveCompletionCursor = false;
        priorSelectionStartByte = selectionStartByte; priorSelectionEndByte = selectionEndByte;
        codeActionShortcut = MayEditSource(document) && InputActive && ((Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.Period, false)) || (Gui.GetIO().KeyAlt && SourceEnterPressed(false)));
        if (InputActive && Gui.IsKeyPressed(ImGuiKey.Escape, false)) { DismissAssistance(); assistRequest = null; return true; }
        if (completion?.Source.Version != document.Version) completion = null;
        if (codeActionShortcut && Gui.GetIO().KeyAlt) flags |= ImGuiInputTextFlags.CtrlEnterForNewLine | ImGuiInputTextFlags.EnterReturnsTrue;
        if (codeActions.Length > 0 && codeActions[0].Source.Version != document.Version) codeActions = [];
        if (completion?.Items.Length > 0)
        {
            flags &= ~ImGuiInputTextFlags.AllowTabInput;
            flags |= ImGuiInputTextFlags.CallbackCompletion | ImGuiInputTextFlags.CallbackCharFilter;
            if (InputActive && Gui.IsKeyPressed(ImGuiKey.DownArrow, false)) { selectedCompletion = Math.Min(selectedCompletion + 1, completion.Items.Length - 1); preserveCompletionCursor = true; ImGuiP.SetKeyOwner(ImGuiKey.DownArrow, Gui.GetID(label + "CompletionNavigation"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame); }
            if (InputActive && Gui.IsKeyPressed(ImGuiKey.UpArrow, false)) { selectedCompletion = Math.Max(selectedCompletion - 1, 0); preserveCompletionCursor = true; ImGuiP.SetKeyOwner(ImGuiKey.UpArrow, Gui.GetID(label + "CompletionNavigation"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame); }
            commitOnEnter = InputActive && !Gui.GetIO().KeyCtrl && SourceEnterPressed(false) && CompletionCommitsEnter();
            if (commitOnEnter) flags |= ImGuiInputTextFlags.CtrlEnterForNewLine | ImGuiInputTextFlags.EnterReturnsTrue;
            else if (InputActive && SourceEnterPressed(false)) completion = null;
        }
        return false;
    }
    private void FinishAssistanceInput(ScriptSourceDocument document, bool changed)
    {
        var caretChanged = caretByte != priorCaretByte;
        var selectionChanged = selectionStartByte != priorSelectionStartByte || selectionEndByte != priorSelectionEndByte;
        if (caretChanged || changed) caretPosition = Encoding.UTF8.GetCharCount(new ReadOnlySpan<byte>(buffer, Math.Clamp(caretByte, 0, bufferLength)));
        if (selectionChanged || changed)
        {
            var start = Encoding.UTF8.GetCharCount(new ReadOnlySpan<byte>(buffer, Math.Clamp(Math.Min(selectionStartByte, selectionEndByte), 0, bufferLength)));
            var end = Encoding.UTF8.GetCharCount(new ReadOnlySpan<byte>(buffer, Math.Clamp(Math.Max(selectionStartByte, selectionEndByte), 0, bufferLength)));
            selection = TextSpan.FromBounds(start, end);
        }
        if (!csharp) return;
        var typed = typedCompletionCommit; typedCompletionCommit = null;
        if (MayEditSource(document) && typed?.Completion is { } original && typed.CommitCharacter is { } typedCharacter
            && document.EditorText == original.Source.Text.Insert(original.Position, typedCharacter.ToString()))
            assistRequest = typed with { Position = caretPosition, Version = document.Version };
        else if (MayEditSource(document) && commitOnEnter && completion != null) assistRequest = new(ScriptAssistKind.CommitCompletion, caretPosition, document.Version, completion, selectedCompletion);
        else if (codeActionShortcut)
            assistRequest = new(ScriptAssistKind.CodeActions, caretPosition, document.Version, Selection: selection);
        else if (InputActive && Gui.IsKeyPressed(ImGuiKey.F12, false)) assistRequest = new(ScriptAssistKind.Definition, caretPosition, document.Version);
        else if (InputActive && Gui.GetIO().KeyCtrl && Gui.IsKeyPressed(ImGuiKey.Space, false) && (Gui.GetIO().KeyShift || MayEditSource(document)))
            assistRequest = new(Gui.GetIO().KeyShift ? ScriptAssistKind.Signature : ScriptAssistKind.Completion, caretPosition, document.Version);
        else if (InputActive && changed && caretPosition > 0)
        {
            var character = document.EditorText[Math.Min(caretPosition - 1, document.EditorText.Length - 1)];
            if (MayEditSource(document) && (character == '.' || (completion != null || completionPending) && (char.IsLetterOrDigit(character) || character == '_')))
                assistRequest = new(ScriptAssistKind.Completion, caretPosition, document.Version);
            else if (character is '(' or ',' || signatures.Length > 0) assistRequest = new(ScriptAssistKind.Signature, caretPosition, document.Version);
            else DismissAssistance();
        }
        else if (InputActive && (caretChanged || selectionChanged))
        {
            completion = null;
            codeActions = [];
            if (signatures.Length > 0) assistRequest = new(ScriptAssistKind.Signature, caretPosition, document.Version);
        }
        if (assistRequest?.Kind == ScriptAssistKind.Completion) { completionPending = true; completion = null; }
        UpdateCodeActionDiscovery(document);
    }
    public void ApplyAssistance(ScriptAssistResult result, ScriptSourceDocument document)
    {
        if (result.Source.Version != document.Version || !string.Equals(result.Source.Path, document.Path, StringComparison.OrdinalIgnoreCase)) { result.CodeActionBatch?.Dispose(); return; }
        if (result.Request.Generation != AssistanceGeneration) { result.CodeActionBatch?.Dispose(); return; }
        if (result.Request.Kind == ScriptAssistKind.QuickInfo ? result.Request.Position != hoveredSourcePosition : result.Request.Position != caretPosition) { result.CodeActionBatch?.Dispose(); return; }
        if (result.Request.Selection is { } requestedSelection && requestedSelection != selection) { result.CodeActionBatch?.Dispose(); return; }
        if (result.QuickInfo != null) { quickInfoText = result.QuickInfo; return; }
        if (result.Braces != null)
        {
            bracePairs = result.Braces; braceVersion = document.Version;
            if (result.Request.Kind == ScriptAssistKind.Braces) return;
        }
        if (result.Description != null) { completionDescription = result.Description; return; }
        if (result.CodeActionBatch != null)
        {
            if (!MayEditSource(document)) { result.CodeActionBatch.Dispose(); return; }
            SetCodeActionPreview(result);
        }
        else if (result.Changes is { } changes)
        {
            if (!MayEditSource(document)) return;
            var text = SourceText.From(document.EditorText).WithChanges(changes).ToString();
            var position = Math.Clamp(result.NewPosition ?? caretPosition, 0, text.Length);
            var normalizedPosition = text[..position].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Length;
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            document.UpdateEditorChanges(changes);
            DismissAssistance(); NavigatePosition(normalizedPosition, text);
        }
        else if (result.Completion != null) { completion = result.Completion.Items.Length == 0 ? null : result.Completion; completionPending = false; selectedCompletion = 0; signatures = []; describedCompletion = -1; completionDescription = ""; }
        else if (result.Signatures != null) { signatures = result.Signatures; selectedSignature = Math.Clamp(selectedSignature, 0, Math.Max(0, signatures.Length - 1)); }
        else if (result.Definitions != null)
        {
            definitions = result.Definitions;
            if (definitions.Length == 1) { SelectedDefinition = definitions[0]; definitions = []; }
        }
        else if (result.Actions != null)
        {
            if (result.Request.Kind == ScriptAssistKind.DiscoverCodeActions) bulbActions = result.Actions;
            else { DismissAssistance(); codeActions = result.Actions; }
        }
    }
    public ScriptDefinition? TakeDefinition() { var value = SelectedDefinition; SelectedDefinition = null; return value; }
    private void DrawAssistance(ScriptSourceDocument document)
    {
        DrawCodeActionPreview(document);
        if (completion == null && signatures.Length == 0 && definitions.Length == 0 && codeActions.Length == 0) return;
        Gui.SetNextWindowPos(assistancePosition, ImGuiCond.Always);
        var height = completion != null || definitions.Length > 0 || codeActions.Length > 0 ? 220 * Gui.GetStyle().FontScaleDpi
            : Gui.GetTextLineHeightWithSpacing() * (signatures.Length > 1 ? 3 : 2) + Gui.GetStyle().WindowPadding.Y * 2;
        Gui.SetNextWindowSize(new Vector2(Math.Min(520 * Gui.GetStyle().FontScaleDpi, Gui.GetIO().DisplaySize.X), height), ImGuiCond.Always);
        if (Gui.Begin("##Script assistance" + label, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.HorizontalScrollbar))
        {
            if (completion is { } items)
            {
                var descriptionWidth = completionDescription.Length != 0 ? Gui.GetContentRegionAvail().X * 0.45f : 0;
                var listOpen = Gui.BeginChild("##CompletionItems" + label,
                    new Vector2(descriptionWidth > 0 ? Gui.GetContentRegionAvail().X - descriptionWidth - Gui.GetStyle().ItemSpacing.X : 0, 0));
                if (listOpen)
                {
                var rowHeight = Gui.GetTextLineHeightWithSpacing();
                if (preserveCompletionCursor) Gui.SetScrollY(Math.Max(0, selectedCompletion * rowHeight - Gui.GetWindowHeight() / 2));
                var first = Math.Clamp((int)(Gui.GetScrollY() / rowHeight), 0, items.Items.Length);
                var last = Math.Min(items.Items.Length, first + (int)(Gui.GetContentRegionAvail().Y / rowHeight) + 2);
                if (first > 0) Gui.Dummy(new Vector2(1, first * rowHeight));
                for (var index = first; index < last; index++)
                {
                    var item = items.Items[index]; Gui.PushID(index);
                    var origin = Gui.GetCursorScreenPos();
                    var selected = Gui.Selectable("##Completion", index == selectedCompletion);
                    AssistanceControlRecorder?.Invoke("script/completion/" + item.DisplayText, selected);
                    if (selected && MayEditSource(document))
                        assistRequest = new(ScriptAssistKind.CommitCompletion, caretPosition, document.Version, items, index);
                    Gui.GetWindowDrawList().AddText(origin, Gui.GetColorU32(ImGuiCol.Text), item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix);
                    if (preserveCompletionCursor && index == selectedCompletion) Gui.SetScrollHereY();
                    Gui.PopID();
                }
                if (last < items.Items.Length) Gui.Dummy(new Vector2(1, (items.Items.Length - last) * rowHeight));
                }
                ImGuiScopes.EndChild();
                if (completionDescription.Length != 0)
                {
                    Gui.SameLine(); semanticInfoText.Draw("##CompletionDescription", completionDescription, true,
                        new Vector2(descriptionWidth, Gui.GetContentRegionAvail().Y));
                }
            }
            else if (signatures.Length > 0)
            {
                if (signatures.Length > 1)
                {
                    if (Gui.SmallButton("<")) selectedSignature = (selectedSignature + signatures.Length - 1) % signatures.Length;
                    Gui.SameLine(); Gui.TextUnformatted($"{selectedSignature + 1} / {signatures.Length}"); Gui.SameLine();
                    if (Gui.SmallButton(">")) selectedSignature = (selectedSignature + 1) % signatures.Length;
                }
                var first = true;
                foreach (var part in signatures[selectedSignature].Parts)
                {
                    if (!first) Gui.SameLine(0, 0); first = false;
                    if (part.Active) Gui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.8f, 1, 1));
                    Gui.TextUnformatted(part.Text);
                    if (part.Active) Gui.PopStyleColor();
                }
            }
            else if (codeActions.Length > 0)
            {
                for (var index = 0; index < codeActions.Length; index++)
                {
                    var action = codeActions[index]; Gui.PushID(index); var origin = Gui.GetCursorScreenPos();
                    if (Gui.Selectable("##CodeAction")) assistRequest = new(ScriptAssistKind.ApplyCodeAction, caretPosition, document.Version, Action: action, Selection: selection);
                    Gui.GetWindowDrawList().AddText(origin, Gui.GetColorU32(ImGuiCol.Text), action.Title);
                    Gui.PopID();
                }
            }
            else foreach (var definition in definitions)
                if (Gui.Selectable($"{Path.GetFileName(definition.Path)}:{definition.Line}:{definition.Column}")) { SelectedDefinition = definition; definitions = []; break; }
        }
        ImGuiScopes.EndWindow();
    }
}
