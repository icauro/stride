using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class OutputPanel
{
    private void DrawOutputSearchInput()
    {
        var state = CurrentOutput;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (NativeStringInput.DrawWithHint("##OutputSearch", EditorLocalization.Text("Search"), ref state.SearchDraft))
            state.SearchValidationDeadline = now + System.Diagnostics.Stopwatch.Frequency / 2;
        WorkflowControlRecorder?.Invoke("output/search");
        if ((Gui.IsItemActive() || Gui.IsItemDeactivated()) && Gui.IsKeyPressed(ImGuiKey.Escape))
        {
            state.SearchDraft = state.Search;
            state.SearchValidationDeadline = 0;
            return;
        }
        var validate = Gui.IsItemDeactivatedAfterEdit()
            || Gui.IsItemActive() && (Gui.IsKeyPressed(ImGuiKey.Enter) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter))
            || state.SearchValidationDeadline != 0 && now >= state.SearchValidationDeadline;
        if (!validate) return;
        AcceptOutputSearch(state);
    }

    private static void AcceptOutputSearch(ChannelState state)
    {
        state.SearchValidationDeadline = 0;
        if (state.Search == state.SearchDraft) return;
        state.Search = state.SearchDraft;
        state.MatchesDirty = state.SearchChanged = true;
    }

    private void PumpOutputSearchValidation()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var state in outputChannels.Values)
            if (state.SearchValidationDeadline != 0 && now >= state.SearchValidationDeadline) AcceptOutputSearch(state);
    }

    private void DrawSearchControls()
    {
        Gui.SameLine(0, UiScale(4));
        using (ImGuiScopes.Disabled(outputMatches.Count == 0 || outputTextDirty || outputMatchesDirty))
        {
            if (DrawSearchIcon("##output-previous", "output-search-previous", 8, false, "Go to previous search result")) SelectOutputMatch(-1);
            Gui.SameLine(0, UiScale(4));
            if (DrawSearchIcon("##output-next", "output-search-next", 8, false, "Go to next search result")) SelectOutputMatch(1);
        }
        var oldCase = outputMatchCase; var oldWord = outputMatchWord;
        Gui.SameLine(0, UiScale(4));
        if (DrawSearchIcon("##output-match-case", "output-search-match-case", 16, outputMatchCase, "Match case")) outputMatchCase = !outputMatchCase;
        Gui.SameLine(0, UiScale(4));
        if (DrawSearchIcon("##output-match-word", "output-search-match-word", 16, outputMatchWord, "Match whole word")) outputMatchWord = !outputMatchWord;
        outputMatchesDirty |= oldCase != outputMatchCase || oldWord != outputMatchWord;
        outputSearchChanged |= oldCase != outputMatchCase || oldWord != outputMatchWord;
    }
    private bool DrawSearchIcon(string identity, string artwork, float logicalSize, bool selected, string tooltip)
    {
        // Native toolbar buttons:20-DIP minima and2-DIP margins on each side.
        // Their content is exact source Geometry, uniformly fitted at8/16 DIPs.
        var size = new System.Numerics.Vector2(UiScale(20));
        Gui.PushStyleColor(ImGuiCol.Button, selected ? Gui.GetStyle().Colors[(int)ImGuiCol.ButtonActive] : System.Numerics.Vector4.Zero);
        bool clicked;
        try { clicked = Gui.Button(identity, size); }
        finally { Gui.PopStyleColor(); }
        if (identity == "##output-next") WorkflowControlRecorder?.Invoke("output/find-next");
        if (currentIcons?.Invoke() is { } icons)
        {
            var actual = icons.Size(artwork);
            var factor = UiScale(logicalSize) / Math.Max(actual.X, actual.Y);
            var image = actual * factor;
            var minimum = Gui.GetItemRectMin() + (size - image) * .5f;
            Gui.GetWindowDrawList().AddImage(icons.Get(artwork), minimum, minimum + image,
                System.Numerics.Vector2.Zero, System.Numerics.Vector2.One, Gui.GetColorU32(ImGuiCol.Text));
        }
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Gui.SetTooltip(EditorLocalization.Text(tooltip, "ToolTip"));
        return clicked;
    }
    private void SelectOutputMatch(int direction)
    {
        if (outputMatches.Count == 0) return;
        outputMatchIndex = (outputMatchIndex + direction + outputMatches.Count) % outputMatches.Count;
        var match = outputMatches[outputMatchIndex];
        // Match selection scrolls the log, as WPF's TextLogViewer does, but
        // must not queue keyboard focus after a subsequent search-field click.
        outputTextView.SelectSourceBytes(match.StartByte, match.LengthByte, focus: false);
    }
    private void RebuildSearchMatches()
    {
        var previous = outputMatchIndex;
        var revealFirst = outputSearchChanged;
        outputMatches.Clear(); outputMatchIndex = -1;
        if (string.IsNullOrEmpty(outputSearch)) return;
        var comparison = outputMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var cursor = 0; var bytePosition = 0;
        foreach (var range in outputMessageRanges)
        {
            bytePosition += Encoding.UTF8.GetByteCount(outputText.AsSpan(cursor, range.Start - cursor));
            cursor = range.Start;
            var rangeEnd = checked(range.Start + range.Length);
            while (cursor < rangeEnd)
            {
                var found = outputText.IndexOf(outputSearch, cursor, rangeEnd - cursor, comparison);
                if (found < 0) break;
                var end = found + outputSearch.Length;
                bytePosition += Encoding.UTF8.GetByteCount(outputText.AsSpan(cursor, found - cursor));
                var length = Encoding.UTF8.GetByteCount(outputText.AsSpan(found, outputSearch.Length));
                // Match the native TextLogViewer's ASCII-letter word boundaries.
                if (!outputMatchWord || (found == range.Start || !IsWordLetter(outputText[found - 1]))
                    && (end == rangeEnd || !IsWordLetter(outputText[end])))
                    outputMatches.Add(new(bytePosition, length));
                bytePosition += length; cursor = end;
            }
            bytePosition += Encoding.UTF8.GetByteCount(outputText.AsSpan(cursor, rangeEnd - cursor));
            cursor = rangeEnd;
        }
        if (outputMatches.Count != 0)
        {
            outputMatchIndex = revealFirst ? 0 : Math.Clamp(previous, 0, outputMatches.Count - 1);
            if (revealFirst)
            {
                var first = outputMatches[0];
                outputTextView.SelectSourceBytes(first.StartByte, first.LengthByte, focus: false);
            }
        }
    }
    private static bool IsWordLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
