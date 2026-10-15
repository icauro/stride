using Microsoft.CodeAnalysis.Text;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private sealed record SourceRectangle(int AnchorLine, float AnchorColumn, int EndLine, float EndColumn);
    private SourceRectangle? rectangle;
    private sealed record RectangleSegment(int Line, TextSpan Span, int Padding);
    private RectangleSegment[] RectangleSegments()
    {
        if (rectangle is not { } box) return [];
        var left = Math.Min(box.AnchorColumn, box.EndColumn) * MeasureGlyph(" ");
        var right = Math.Max(box.AnchorColumn, box.EndColumn) * MeasureGlyph(" ");
        var segments = new List<RectangleSegment>();
        for (var line = Math.Max(0, Math.Min(box.AnchorLine, box.EndLine)); line <= Math.Min(lines.Length - 1, Math.Max(box.AnchorLine, box.EndLine)); line++)
        {
            var padding = Math.Max(0, (int)MathF.Round((left - SourceGeometry(line).Width) / Math.Max(1, MeasureGlyph(" "))));
            foreach (var span in SourceGeometry(line).VisualSpans(left, right))
                segments.Add(new(line, TextSpan.FromBounds(lineStarts[line] + span.Start, lineStarts[line] + span.End), padding));
        }
        return segments.ToArray();
    }
    private void SyncRectangleCaret(ScriptSourceDocument document)
    {
        if (rectangle is not { } box) return;
        var row = Math.Clamp(box.EndLine, 0, lines.Length - 1);
        var position = lineStarts[row] + SourceHit(row, box.EndColumn * MeasureGlyph(" "));
        var anchorRow = Math.Clamp(box.AnchorLine, 0, lines.Length - 1);
        SetSourceCaret(document, position, lineStarts[anchorRow] + SourceHit(anchorRow, box.AnchorColumn * MeasureGlyph(" ")));
    }
    private void MoveSourceRectangle(ScriptSourceDocument document)
    {
        var row = SourceRow(caretPosition); var column = SourceX(row, caretPosition - lineStarts[row]) / Math.Max(1, MeasureGlyph(" "));
        var anchorRow = SourceRow(sourceAnchor);
        var box = rectangle ?? new(anchorRow, SourceX(anchorRow, sourceAnchor - lineStarts[anchorRow]) / Math.Max(1, MeasureGlyph(" ")), row, column);
        var endLine = box.EndLine; var endColumn = box.EndColumn;
        if (SourcePressed(Hexa.NET.ImGui.ImGuiKey.UpArrow)) endLine--;
        else if (SourcePressed(Hexa.NET.ImGui.ImGuiKey.DownArrow)) endLine++;
        else if (SourcePressed(Hexa.NET.ImGui.ImGuiKey.Home) || SourcePressed(Hexa.NET.ImGui.ImGuiKey.End))
        {
            var targetRow = Math.Clamp(endLine, 0, lines.Length - 1);
            var offset = lineStarts[targetRow] + SourceHit(targetRow, endColumn * MeasureGlyph(" "));
            offset = SourcePressed(Hexa.NET.ImGui.ImGuiKey.Home) ? SourceLineHome(targetRow, offset) : lineStarts[targetRow] + lines[targetRow].Length;
            endColumn = SourceX(targetRow, offset - lineStarts[targetRow]) / Math.Max(1, MeasureGlyph(" "));
        }
        else if (SourcePressed(Hexa.NET.ImGui.ImGuiKey.LeftArrow) || SourcePressed(Hexa.NET.ImGui.ImGuiKey.RightArrow))
        {
            var targetRow = Math.Clamp(endLine, 0, lines.Length - 1); var x = endColumn * MeasureGlyph(" ");
            var backwards = SourcePressed(Hexa.NET.ImGui.ImGuiKey.LeftArrow);
            if (x > SourceGeometry(targetRow).Width) endColumn += backwards ? -1 : 1;
            else
            {
                var offset = lineStarts[targetRow] + SourceHit(targetRow, x);
                if (Gui.GetIO().KeyCtrl)
                {
                    offset = Math.Clamp(SourceWord(document.EditorText, offset, !backwards), lineStarts[targetRow], lineStarts[targetRow] + lines[targetRow].Length);
                    endColumn = SourceX(targetRow, offset - lineStarts[targetRow]) / Math.Max(1, MeasureGlyph(" "));
                }
                else
                {
                    var stops = SourceGeometry(targetRow).Native.VisualCarets();
                    var targets = stops.Where(stop => backwards ? stop.X < x - .01f : stop.X > x + .01f);
                    if (targets.Any()) endColumn = (backwards ? targets.Last().X : targets.First().X) / Math.Max(1, MeasureGlyph(" "));
                    else endColumn += backwards ? -1 : 1;
                }
            }
        }
        rectangle = box with { EndLine = Math.Clamp(endLine, 0, lines.Length - 1), EndColumn = Math.Max(0, endColumn) };
        SyncRectangleCaret(document);
    }
    private void ReplaceRectangle(ScriptSourceDocument document, string content, int deletionDirection = 0)
    {
        if (!MayEditSource(document) || rectangle is not { } box) return;
        var segments = RectangleSegments();
        if (segments.Length == 0) return;
        var rows = segments.GroupBy(segment => segment.Line).ToArray();
        var multiline = content.Contains('\n');
        string[] values = multiline ? content.Split(['\n'], rows.Length, StringSplitOptions.None) : [content];
        var changes = new List<TextChange>();
        for (var index = 0; index < rows.Length && (!multiline || index < values.Length); index++)
        {
            var firstSegment = true;
            foreach (var segment in rows[index])
            {
            var value = firstSegment ? values[multiline ? index : 0] : ""; var span = segment.Span;
            if (span.Length == 0 && content.Length == 0 && deletionDirection != 0 && segment.Padding == 0)
            {
                var start = span.Start; var end = start;
                if (deletionDirection < 0) start = Math.Max(lineStarts[segment.Line], PreviousSourceCodepoint(document.EditorText, start));
                else end = Math.Min(lineStarts[segment.Line] + lines[segment.Line].Length, NextSourcePosition(end, document.EditorText.Length));
                span = TextSpan.FromBounds(start, end);
            }
            if (value.Length > 0 && segment.Padding > 0) value = new string(' ', segment.Padding) + value;
            if (span.Length > 0 || value.Length > 0) changes.Add(new(span, value));
            firstSegment = false;
            }
        }
        if (changes.Count == 0)
        {
            if (deletionDirection < 0) rectangle = box with { AnchorColumn = Math.Max(0, box.AnchorColumn - 1), EndColumn = Math.Max(0, box.EndColumn - 1) };
            SyncRectangleCaret(document); return;
        }
        using var transaction = document.SessionHistory?.CreateTransaction("Edit rectangular source selection");
        document.UpdateEditorChanges(changes); Load(document.EditorText); version = document.Version;
        var first = changes[0]; var topLine = Math.Min(box.AnchorLine, box.EndLine);
        var position = Math.Clamp(first.Span.Start + (first.NewText?.Length ?? 0), 0, document.EditorText.Length);
        if (multiline)
        {
            var row = SourceRow(position); var x = SourceX(row, position - lineStarts[row]);
            var bottom = Math.Clamp(Math.Max(box.AnchorLine, box.EndLine), 0, lines.Length - 1);
            var target = lineStarts[bottom] + SourceHit(bottom, x);
            rectangle = null; SetSourceCaret(document, target, target);
        }
        else
        {
            var row = SourceRow(position); var column = SourceX(row, position - lineStarts[row]) / Math.Max(1, MeasureGlyph(" "));
            rectangle = new(topLine, column, Math.Min(Math.Max(box.AnchorLine, box.EndLine), lines.Length - 1), column); SyncRectangleCaret(document);
        }
        sourceReveal = true;
    }
    private bool CopyRectangle(ScriptSourceDocument document, bool cut)
    {
        if (rectangle == null) return false;
        var segments = RectangleSegments();
        var value = string.Join(Environment.NewLine, segments.GroupBy(segment => segment.Line)
            .Select(row => string.Concat(row.Select(segment => document.CopySourceRange(segment.Span.Start, segment.Span.Length)))));
        if (WindowsScriptClipboard.TryWrite((nint)Gui.GetWindowViewport().PlatformHandleRaw, value, false, rectangle: true)
            && cut && MayEditSource(document)) ReplaceRectangle(document, "");
        return true;
    }
    private bool PasteRectangle(ScriptSourceDocument document, string value, bool rectangleClipboard)
    {
        if (rectangle != null) { ReplaceRectangle(document, value); return true; }
        if (!rectangleClipboard || selection.Length > 0) return false;
        var row = SourceRow(caretPosition); var count = value.Count(c => c == '\n');
        // Reference PerformRectangularPaste falls back to normal paste if its
        // destination rows do not exist; it never invents new physical sources.
        if (row + count >= lines.Length) return false;
        var column = SourceX(row, caretPosition - lineStarts[row]) / Math.Max(1, MeasureGlyph(" "));
        rectangle = new(row, column, row + count, column); ReplaceRectangle(document, value); return true;
    }
}
