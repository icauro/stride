using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using Microsoft.CodeAnalysis.Text;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private bool searchOpen, replaceMode, searchCase, searchWords, searchRegex, focusSearch, focusSource;
    private string searchPattern = "", replacePattern = "";
    private ScriptSearchSnapshot? requestedSearch;
    private ScriptSearchResult? searchResult;
    private Task<ScriptSearchResult>? searchTask;
    private CancellationTokenSource? searchCancellation;
    private int selectedSearch = -1;
    private (int Start, int End)? pendingSelection;
    private bool pendingSelectionCaretAtEnd;
    private int? searchScrollLine;
    private readonly EditorSelectableText searchMessage = new();
    private readonly List<SearchHighlight> searchHighlights = [];
    private string? replacementError;

    private void DrawSearch(ScriptSourceDocument document)
    {
        var focused = Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var io = Gui.GetIO();
        if (focused && io.KeyCtrl && (Gui.IsKeyPressed(ImGuiKey.F, false) || Gui.IsKeyPressed(ImGuiKey.H, false)))
        {
            searchOpen = true; replaceMode = Gui.IsKeyPressed(ImGuiKey.H, false); focusSearch = true;
            if (selection.Length > 0 && selection.End <= document.EditorText.Length)
            {
                var text = document.EditorText.Substring(selection.Start, selection.Length);
                if (!text.Contains('\n')) searchPattern = text;
            }
            DismissAssistance();
        }
        if (!searchOpen) return;
        if (focused && Gui.IsKeyPressed(ImGuiKey.Escape, false)) { CloseSearch(); return; }
        PumpSearch(document);
        Gui.PushID(label + "Search");
        if (Gui.ArrowButton("Mode", replaceMode ? ImGuiDir.Down : ImGuiDir.Right)) replaceMode = !replaceMode;
        if (Gui.IsItemHovered()) Gui.SetTooltip("Toggle between find and replace modes");
        Gui.SameLine();
        if (focusSearch) { Gui.SetKeyboardFocusHere(); focusSearch = false; }
        Gui.SetNextItemWidth(Math.Min(200 * Gui.GetStyle().FontScaleDpi, Math.Max(60, Gui.GetContentRegionAvail().X * 0.35f)));
        Gui.InputText("##Search", ref searchPattern, 16384, ImGuiInputTextFlags.AutoSelectAll);
        var searchHovered = Gui.IsItemHovered(); var searchActive = Gui.IsItemActive();
        Gui.SameLine();
        if (Gui.Button("Options")) Gui.OpenPopup("Search options");
        if (Gui.BeginPopup("Search options"))
        {
            Gui.Checkbox(EditorLocalization.Widget("Match case", "ToolTip"), ref searchCase); Gui.Checkbox("Match whole words", ref searchWords);
            Gui.Checkbox("Use regular expressions", ref searchRegex); Gui.EndPopup();
        }
        Gui.SameLine();
        if (Gui.ArrowButton("Previous", ImGuiDir.Up)) FindSearch(document, true);
        if (Gui.IsItemHovered()) Gui.SetTooltip("Find previous (Shift+F3)");
        Gui.SameLine();
        if (Gui.ArrowButton("Next", ImGuiDir.Down)) FindSearch(document, false);
        if (Gui.IsItemHovered()) Gui.SetTooltip("Find next (F3)");
        Gui.SameLine();
        if (Gui.SmallButton(EditorLocalization.Widget("X", "Button"))) { CloseSearch(); Gui.PopID(); return; }
        if (replaceMode && searchOpen)
        {
            Gui.SetNextItemWidth(Math.Min(200 * Gui.GetStyle().FontScaleDpi, Math.Max(60, Gui.GetContentRegionAvail().X * 0.35f)));
            Gui.InputText("##Replacement", ref replacePattern, 16384);
            Gui.SameLine(); Gui.BeginDisabled(!MayEditSource(document) || !CurrentSearch(document));
            if (Gui.Button("Replace next")) ReplaceSearch(document, false);
            if (Gui.IsItemHovered()) Gui.SetTooltip("Replace next (Alt+R)");
            Gui.SameLine();
            if (Gui.Button("Replace all")) ReplaceSearch(document, true);
            if (Gui.IsItemHovered()) Gui.SetTooltip("Replace all (Alt+A)");
            Gui.EndDisabled();
        }
        if (focused && (Gui.IsKeyPressed(ImGuiKey.F3, false) || searchActive && SourceEnterPressed(false))) FindSearch(document, io.KeyShift);
        if (focused && replaceMode && io.KeyAlt && MayEditSource(document))
        {
            if (Gui.IsKeyPressed(ImGuiKey.R, false)) ReplaceSearch(document, false);
            else if (Gui.IsKeyPressed(ImGuiKey.A, false)) ReplaceSearch(document, true);
        }
        PumpSearch(document);
        var error = replacementError ?? (CurrentSearch(document) ? searchResult!.Error : null);
        if (searchHovered && (error != null || CurrentSearch(document) && searchPattern.Length > 0 && searchResult!.Matches.Length == 0))
        {
            Gui.BeginTooltip(); searchMessage.Draw("##SearchMessage", error == null ? "No matches found!" : "Error: " + error,
                true, new Vector2(400 * Gui.GetStyle().FontScaleDpi, Gui.GetTextLineHeightWithSpacing() * 3)); Gui.EndTooltip();
        }
        Gui.PopID();
    }
    private bool CurrentSearch(ScriptSourceDocument document) => searchResult?.Snapshot.Version == document.Version
        && ReferenceEquals(searchResult.Snapshot.Text, document.EditorText) && searchResult.Snapshot.Pattern == searchPattern
        && searchResult.Snapshot.MatchCase == searchCase && searchResult.Snapshot.WholeWords == searchWords && searchResult.Snapshot.Regex == searchRegex;
    private void PumpSearch(ScriptSourceDocument document)
    {
        var next = new ScriptSearchSnapshot(document.EditorText, document.Version, searchPattern, searchCase, searchWords, searchRegex);
        if (requestedSearch != next)
        {
            StopSearchWork(); requestedSearch = next; searchResult = null; selectedSearch = -1; searchHighlights.Clear(); replacementError = null;
            searchCancellation = new(); var cancellation = searchCancellation.Token;
            searchTask = Task.Run(() => ScriptSourceSearch.Find(next, cancellation), cancellation);
        }
        if (searchTask?.IsCompleted != true) return;
        var completed = searchTask; searchTask = null; searchCancellation?.Dispose(); searchCancellation = null;
        try
        {
            var result = completed.GetAwaiter().GetResult();
            if (result.Snapshot != requestedSearch) return;
            searchResult = result;
            var text = SourceText.From(result.Snapshot.Text);
            for (var index = 0; index < result.Matches.Length; index++)
            {
                var match = result.Matches[index];
                var span = text.Lines.GetLinePositionSpan(new TextSpan(match.Index, match.Length));
                for (var line = span.Start.Line; line <= span.End.Line; line++)
                    searchHighlights.Add(new(index, line, line == span.Start.Line ? span.Start.Character : 0,
                        line == span.End.Line ? span.End.Character : text.Lines[line].Span.Length));
            }
            var first = Array.FindIndex(result.Matches, match => match.Index >= caretPosition);
            if (first >= 0) SelectSearch(document, first);
        }
        catch (OperationCanceledException) { }
    }
    private void FindSearch(ScriptSourceDocument document, bool previous)
    {
        if (!CurrentSearch(document) || searchResult!.Matches.Length == 0) return;
        var matches = searchResult.Matches;
        var selected = selectedSearch >= 0 && selectedSearch < matches.Length ? matches[selectedSearch] : null;
        var result = previous ? Array.FindLastIndex(matches, match => match.Index + match.Length <= caretPosition && !ReferenceEquals(match, selected))
            : Array.FindIndex(matches, match => match.Index >= caretPosition && !ReferenceEquals(match, selected));
        if (result < 0) result = previous ? matches.Length - 1 : 0;
        SelectSearch(document, result);
    }
    private void SelectSearch(ScriptSourceDocument document, int index)
    {
        selectedSearch = index;
        var found = searchResult!.Matches[index];
        caretPosition = found.Index; selection = new(found.Index, found.Length);
        caretByte = selectionStartByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, found.Index));
        selectionEndByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, found.Index + found.Length));
        pendingSelection = (selectionStartByte, selectionEndByte);
        pendingSelectionCaretAtEnd = false;
        searchScrollLine = SourceText.From(document.EditorText).Lines.GetLineFromPosition(found.Index).LineNumber;
    }
    private void ReplaceSearch(ScriptSourceDocument document, bool all)
    {
        if (!MayEditSource(document) || !CurrentSearch(document) || searchResult!.Error != null) return;
        try
        {
            var matches = all ? searchResult.Matches : searchResult.Matches.Where(match => selection.Start == match.Index && selection.Length == match.Length && match.Length > 0).Take(1).ToArray();
            if (matches.Length > 0)
            {
                var changes = matches.Select(match => new TextChange(new TextSpan(match.Index, match.Length),
                    match.Result(replacePattern).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))).ToArray();
                using var transaction = document.SessionHistory?.CreateTransaction(all ? "Replace all" : "Replace next");
                document.UpdateEditorChanges(changes);
                caretPosition = Math.Clamp(all ? caretPosition : matches[0].Index + (changes[0].NewText?.Length ?? 0), 0, document.EditorText.Length);
                selection = new(caretPosition, 0);
                caretByte = selectionStartByte = selectionEndByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, caretPosition));
                if (!CurrentSearch(document)) selectedSearch = -1;
                pendingSelection = (caretByte, caretByte);
            }
            if (!all)
            {
                if (CurrentSearch(document)) FindSearch(document, false);
                else PumpSearch(document);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or EncoderFallbackException) { replacementError = error.Message; }
    }
    private void CloseSearch()
    {
        searchOpen = false; searchHighlights.Clear(); requestedSearch = null; searchResult = null; focusSource = true; StopSearchWork();
    }
    private void StopSearchWork()
    {
        searchCancellation?.Cancel(); var task = searchTask; var cancellation = searchCancellation;
        searchTask = null; searchCancellation = null;
        if (task == null) cancellation?.Dispose();
        else _ = task.ContinueWith(completed => { _ = completed.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
    }
    private void DrawSearchHighlights(ImGuiWindowPtr child, ImDrawListPtr draw, Vector2 origin, Vector2 clipMin, Vector2 clipMax, float lineHeight)
    {
        if (!searchOpen || searchResult?.Snapshot.Version != version) return;
        if (searchScrollLine is { } target)
        {
            var y = target * lineHeight + Gui.GetStyle().FramePadding.Y;
            if (y < child.Scroll.Y || y + lineHeight > child.Scroll.Y + clipMax.Y - clipMin.Y) ImGuiP.SetScrollY(child, Math.Max(0, y - lineHeight));
            searchScrollLine = null;
        }
        var firstLine = Math.Max(0, (int)((clipMin.Y - origin.Y) / lineHeight) - 1);
        var low = 0; var high = searchHighlights.Count;
        while (low < high) { var middle = (low + high) / 2; if (searchHighlights[middle].Line < firstLine) low = middle + 1; else high = middle; }
        for (var index = low; index < searchHighlights.Count; index++)
        {
            var hit = searchHighlights[index];
            var y = origin.Y + hit.Line * lineHeight;
            if (y + lineHeight < clipMin.Y) continue;
            if (y > clipMax.Y) break;
            var ranges = SourceRangeRectangles(hit.Line, hit.Start, hit.End);
            if (ranges.Length == 0) ranges = [(new Vector2(SourceX(hit.Line, hit.Start), 0), new Vector2(SourceX(hit.Line, hit.Start) + 2, lineHeight))];
            foreach (var range in ranges)
            {
                var start = new Vector2(origin.X + range.Minimum.X, y); var end = new Vector2(origin.X + range.Maximum.X, y + lineHeight);
                draw.AddRectFilled(start, end, hit.MatchIndex == selectedSearch ? 0x805080d0 : 0x3032cd32); draw.AddRect(start, end, 0xff32cd32);
            }
        }
    }
    private sealed record SearchHighlight(int MatchIndex, int Line, int Start, int End)
    {
        internal float X, Width, FontSize = -1;
    }
}
