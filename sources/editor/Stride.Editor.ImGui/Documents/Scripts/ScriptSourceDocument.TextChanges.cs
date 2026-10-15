using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace Stride.Editor.ImGui;

internal partial class ScriptSourceDocument
{
    /// <summary>Applies real normalized source spans while preserving raw separators outside them.</summary>
    internal void UpdateEditorChanges(IEnumerable<TextChange> changes)
    {
        if (!CanEditText) throw new InvalidOperationException("This source file is read-only.");
        var ordered = changes.Select(change => new TextChange(change.Span, Normalize(change.NewText ?? "")))
            .OrderBy(change => change.Span.Start).ToArray();
        if (ordered.Length == 0) return;
        // Roslyn validates source bounds and overlapping spans before any authored mutation.
        SourceText.From(EditorText).WithChanges(ordered);
        var rawSource = SourceText.From(Text);
        var output = new StringBuilder(Text.Length);
        var raw = 0; var normalized = 0; var copied = 0;
        int Advance(int target)
        {
            while (normalized < target)
            {
                if (Text[raw++] == '\r' && raw < Text.Length && Text[raw] == '\n') raw++;
                normalized++;
            }
            return raw;
        }
        foreach (var change in ordered)
        {
            var start = Advance(change.Span.Start);
            var line = rawSource.Lines.GetLineFromPosition(start);
            if (line.SpanIncludingLineBreak.Length == line.Span.Length && line.LineNumber > 0) line = rawSource.Lines[line.LineNumber - 1];
            var separator = line.SpanIncludingLineBreak.Length > line.Span.Length
                ? Text.Substring(line.End, line.SpanIncludingLineBreak.End - line.End) : Environment.NewLine;
            output.Append(Text, copied, start - copied);
            output.Append((change.NewText ?? "").Replace("\n", separator, StringComparison.Ordinal));
            copied = Advance(change.Span.End);
        }
        output.Append(Text, copied, Text.Length - copied);
        // One actual raw-text operation owns exact before/after bytes and compensates failed publication.
        UpdateRawText(output.ToString());
    }
}
