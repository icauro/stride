using System.Globalization;
using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using Microsoft.CodeAnalysis.Text;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private char? handledSourceCharacter;
    private string gutterCountText = "1";
    private float sourceGutterWidth;
    private readonly Dictionary<int, string> lineNumberText = [];
    private int? gutterSelectionAnchor;
    private bool overwriteSource;

    private void PrepareSourceCommands(ScriptSourceDocument document)
    {
        handledSourceCharacter = null;
        if (!InputActive || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return;
        var io = Gui.GetIO();
        if (io.KeyCtrl && (Gui.IsKeyPressed(ImGuiKey.C, false) || Gui.IsKeyPressed(ImGuiKey.Insert, false)))
        { ClaimSourceKey(Gui.IsKeyPressed(ImGuiKey.Insert, false) ? ImGuiKey.Insert : ImGuiKey.C); CopySource(document, false); }
        else if (MayEditSource(document) && ((io.KeyCtrl && Gui.IsKeyPressed(ImGuiKey.X, false)) || (io.KeyShift && !io.KeyCtrl && Gui.IsKeyPressed(ImGuiKey.Delete, false))))
        { ClaimSourceKey(Gui.IsKeyPressed(ImGuiKey.Delete, false) ? ImGuiKey.Delete : ImGuiKey.X); CopySource(document, true); }
        else if (MayEditSource(document) && (io.KeyCtrl && Gui.IsKeyPressed(ImGuiKey.V, false) || io.KeyShift && !io.KeyCtrl && Gui.IsKeyPressed(ImGuiKey.Insert, false)))
        { ClaimSourceKey(Gui.IsKeyPressed(ImGuiKey.Insert, false) ? ImGuiKey.Insert : ImGuiKey.V); PasteSource(document); }
        else if (MayEditSource(document) && !io.KeyCtrl && !io.KeyAlt && completion == null && !completionPending)
        {
            if (!io.KeyShift && Gui.IsKeyPressed(ImGuiKey.Insert, false)) { ClaimSourceKey(ImGuiKey.Insert); overwriteSource = !overwriteSource; }
            else if (Gui.IsKeyPressed(ImGuiKey.Tab, true))
            {
                ClaimSourceKey(ImGuiKey.Tab); handledSourceCharacter = '\t'; IndentSource(document, io.KeyShift);
            }
            else if (SourceEnterPressed(true))
            {
                ClaimSourceKey(Gui.IsKeyPressed(ImGuiKey.KeypadEnter, true) ? ImGuiKey.KeypadEnter : ImGuiKey.Enter); handledSourceCharacter = '\n'; EnterSourceLine(document);
            }
        }
    }
    private void ClaimSourceKey(ImGuiKey key)
        => ImGuiP.SetKeyOwner(key, Gui.GetID(label + "SourceCommand"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
    private void DrawSourceContextMenu(ScriptSourceDocument document)
    {
        if (!Gui.BeginPopupContextItem(label + "Clipboard")) return;
        bool Item(string text, string shortcut, bool enabled = true) => NativeMenuItem?.Invoke(text, shortcut, false, enabled) ?? Gui.MenuItem(text, shortcut, false, enabled);
        if (Item("Cut", "Ctrl+X", MayEditSource(document))) CopySource(document, true);
        if (Item("Copy", "Ctrl+C")) CopySource(document, false);
        if (Item("Paste", "Ctrl+V", MayEditSource(document))) PasteSource(document);
        Gui.EndPopup();
    }
    private void CopySource(ScriptSourceDocument document, bool cut)
    {
        if (CopyRectangle(document, cut)) return;
        var text = SourceText.From(document.EditorText);
        var span = selection.End <= text.Length ? selection : new TextSpan(Math.Clamp(caretPosition, 0, text.Length), 0);
        var wholeLine = span.Length == 0;
        if (wholeLine) span = text.Lines.GetLineFromPosition(Math.Clamp(caretPosition, 0, text.Length)).SpanIncludingLineBreak;
        if (!WindowsScriptClipboard.TryWrite((nint)Gui.GetWindowViewport().PlatformHandleRaw, document.CopySourceRange(span.Start, span.Length), wholeLine,
            SourceClipboardHtml(document, span))) return;
        if (cut && MayEditSource(document)) ApplySourceCommand(document, [new(span, "")], "Cut source", span.Start, span.Start);
    }
    private byte[] SourceClipboardHtml(ScriptSourceDocument document, TextSpan selected)
    {
        var fragment = new StringBuilder("<pre style=\"font-family:monospace\">");
        var position = selected.Start;
        if (version == document.Version)
            foreach (var span in colored)
            {
                var start = lineStarts[span.Line] + span.Column; var end = start + span.Text.Length;
                if (end <= position) continue;
                if (start >= selected.End) break;
                if (start > position) fragment.Append(ClipboardHtmlEncode(document.EditorText[position..Math.Min(start, selected.End)]));
                start = Math.Max(start, position); end = Math.Min(end, selected.End);
                var color = ((span.Color & 255) << 16) | (span.Color & 0xff00) | ((span.Color >> 16) & 255);
                fragment.Append("<span style=\"color:#").Append(color.ToString("x6", CultureInfo.InvariantCulture)).Append("\">")
                    .Append(ClipboardHtmlEncode(document.EditorText[start..end])).Append("</span>");
                position = end;
            }
        fragment.Append(ClipboardHtmlEncode(document.EditorText[position..selected.End])).Append("</pre>");
        const string headerTemplate = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string startHtml = "<html><body><!--StartFragment-->";
        const string endHtml = "<!--EndFragment--></body></html>";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0));
        var startFragment = headerLength + Encoding.UTF8.GetByteCount(startHtml);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment.ToString());
        var htmlEnd = endFragment + Encoding.UTF8.GetByteCount(endHtml);
        return Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture, headerTemplate, headerLength, htmlEnd, startFragment, endFragment)
            + startHtml + fragment + endHtml + '\0');
    }
    private static string ClipboardHtmlEncode(string value) => System.Net.WebUtility.HtmlEncode(value).Replace("\0", "&#0;", StringComparison.Ordinal);
    private void PasteSource(ScriptSourceDocument document)
    {
        if (!MayEditSource(document) || WindowsScriptClipboard.Read((nint)Gui.GetWindowViewport().PlatformHandleRaw) is not { } clipboard) return;
        var content = clipboard.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal);
        if (content.Length == 0) return;
        if (PasteRectangle(document, content, clipboard.Rectangle)) return;
        var source = SourceText.From(document.EditorText);
        var span = selection.End <= source.Length ? selection : new TextSpan(Math.Clamp(caretPosition, 0, source.Length), 0);
        if (clipboard.WholeLine) span = new(source.Lines.GetLineFromPosition(Math.Clamp(caretPosition, 0, source.Length)).Start, 0);
        ApplySourceCommand(document, [new(span, content)], "Paste source", span.Start + content.Length, span.Start + content.Length);
    }
    private void EnterSourceLine(ScriptSourceDocument document)
    {
        if (rectangle != null) { ReplaceRectangle(document, "\n"); return; }
        var source = SourceText.From(document.EditorText);
        var span = selection.End <= source.Length ? selection : new TextSpan(Math.Clamp(caretPosition, 0, source.Length), 0);
        var line = source.Lines.GetLineFromPosition(span.Start);
        var indentationEnd = line.Start;
        while (indentationEnd < Math.Min(span.Start, line.End) && document.EditorText[indentationEnd] is ' ' or '\t') indentationEnd++;
        var inserted = "\n" + document.EditorText[line.Start..indentationEnd];
        ApplySourceCommand(document, [new(span, inserted)], "Insert line break", span.Start + inserted.Length, span.Start + inserted.Length);
    }
    private void IndentSource(ScriptSourceDocument document, bool remove)
    {
        if (rectangle != null) { if (remove) ReplaceRectangle(document, "", -1); else ReplaceRectangle(document, "    "); return; }
        var source = SourceText.From(document.EditorText);
        var span = selection.End <= source.Length ? selection : new TextSpan(Math.Clamp(caretPosition, 0, source.Length), 0);
        var first = source.Lines.GetLineFromPosition(span.Start); var last = source.Lines.GetLineFromPosition(span.End);
        if (!remove && first.LineNumber == last.LineNumber)
        {
            var column = Math.Clamp(caretPosition - first.Start, 0, first.Span.Length);
            var inserted = new string(' ', 4 - column % 4);
            ApplySourceCommand(document, [new(span, inserted)], "Indent source", span.Start + inserted.Length, span.Start + inserted.Length); return;
        }
        if (span.Length > 0 && last.Start == span.End && first.LineNumber != last.LineNumber) last = source.Lines[last.LineNumber - 1];
        var edits = new List<TextChange>();
        for (var index = first.LineNumber; index <= last.LineNumber; index++)
        {
            var line = source.Lines[index];
            if (!remove) { edits.Add(new(new(line.Start, 0), "    ")); continue; }
            var length = 0;
            if (line.Span.Length > 0 && document.EditorText[line.Start] == '\t') length = 1;
            else while (length < Math.Min(4, line.Span.Length) && document.EditorText[line.Start + length] == ' ') length++;
            if (length > 0) edits.Add(new(new(line.Start, length), ""));
        }
        var changes = edits.ToArray();
        var start = MapSourcePosition(span.Start, changes, true); var end = MapSourcePosition(span.End, changes, false);
        if (span.Length == 0) start = end = MapSourcePosition(caretPosition, changes, false);
        ApplySourceCommand(document, changes, remove ? "Unindent source" : "Indent source", start, end);
    }
    private static int MapSourcePosition(int position, TextChange[] changes, bool beforeInsertion)
    {
        var delta = 0;
        foreach (var change in changes)
        {
            if (position < change.Span.Start || position == change.Span.Start && beforeInsertion) break;
            if (position < change.Span.End) return change.Span.Start + delta + (change.NewText?.Length ?? 0);
            delta += (change.NewText?.Length ?? 0) - change.Span.Length;
        }
        return position + delta;
    }
    private void ApplySourceCommand(ScriptSourceDocument document, TextChange[] changes, string title, int start, int end)
    {
        if (!MayEditSource(document) || changes.Length == 0) return;
        var reverse = selection.Length > 0 && caretPosition == selection.Start;
        using var transaction = document.SessionHistory?.CreateTransaction(title);
        document.UpdateEditorChanges(changes);
        start = Math.Clamp(start, 0, document.EditorText.Length); end = Math.Clamp(end, start, document.EditorText.Length);
        caretPosition = reverse ? start : end; selection = TextSpan.FromBounds(start, end);
        selectionStartByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, reverse ? end : start));
        caretByte = selectionEndByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, caretPosition));
        pendingSelection = (selectionStartByte, selectionEndByte);
        pendingSelectionCaretAtEnd = true;
        focusSource = true; selectedSearch = -1;
        DismissAssistance();
    }
    private void DrawSourceLineNumbers(ScriptSourceDocument document, ImGuiWindowPtr child, ImDrawListPtr draw, Vector2 origin, Vector2 clipMin, Vector2 clipMax, float lineHeight)
    {
        var low = new Vector2(sourceOrigin.X + 22 * Gui.GetStyle().FontScaleDpi, clipMin.Y);
        var high = new Vector2(sourceOrigin.X + sourceGutterWidth, clipMax.Y);
        if (Gui.IsWindowHovered() && Gui.IsMouseHoveringRect(low, high) && Gui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            gutterSelectionAnchor = Math.Clamp((int)((Gui.GetMousePos().Y - origin.Y) / lineHeight), 0, lines.Length - 1);
            ImGuiP.SetKeyOwner(ImGuiKey.MouseLeft, Gui.GetID(label + "LineSelection"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockUntilRelease);
            DismissAssistance();
        }
        if (gutterSelectionAnchor is { } anchor)
        {
            if (!ImGuiP.IsMouseDown(ImGuiMouseButton.Left, Gui.GetID(label + "LineSelection"))) gutterSelectionAnchor = null;
            else
            {
                var line = Math.Clamp((int)((Gui.GetMousePos().Y - origin.Y) / lineHeight), 0, lines.Length - 1);
                var source = SourceText.From(document.EditorText);
                var anchorRange = source.Lines[Math.Min(anchor, source.Lines.Count - 1)].SpanIncludingLineBreak;
                var target = source.Lines[Math.Min(line, source.Lines.Count - 1)].SpanIncludingLineBreak;
                var start = line < anchor ? anchorRange.End : anchorRange.Start;
                var end = line < anchor ? target.Start : target.End;
                caretPosition = end; selection = TextSpan.FromBounds(Math.Min(start, end), Math.Max(start, end));
                selectionStartByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, start));
                caretByte = selectionEndByte = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, end));
                pendingSelection = (selectionStartByte, selectionEndByte); pendingSelectionCaretAtEnd = true; focusSource = true;
                if (Gui.GetMousePos().Y < clipMin.Y) ImGuiP.SetScrollY(child, Math.Max(0, child.Scroll.Y - lineHeight));
                else if (Gui.GetMousePos().Y > clipMax.Y) ImGuiP.SetScrollY(child, child.Scroll.Y + lineHeight);
            }
        }
        draw.PushClipRect(new(sourceOrigin.X, clipMin.Y), new(sourceOrigin.X + sourceGutterWidth, clipMax.Y), true);
        var first = Math.Max(0, (int)((clipMin.Y - origin.Y) / lineHeight));
        for (var line = first; line < lines.Length && origin.Y + line * lineHeight < clipMax.Y; line++)
        {
            if (!lineNumberText.TryGetValue(line, out var number))
            {
                if (lineNumberText.Count > 512) lineNumberText.Clear();
                lineNumberText.Add(line, number = (line + 1).ToString(CultureInfo.InvariantCulture));
            }
            var x = sourceOrigin.X + sourceGutterWidth - Gui.GetStyle().ItemSpacing.X - Gui.CalcTextSize(number).X;
            draw.AddText(new(x, origin.Y + line * lineHeight), Gui.GetColorU32(ImGuiCol.TextDisabled), number);
        }
        draw.PopClipRect();
    }
}
