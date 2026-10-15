using System.Runtime.InteropServices;
using System.Text;
using Hexa.NET.ImGui;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Numerics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Length-aware source storage and retained native source geometry; canonical documents own all text/history.</summary>
internal sealed unsafe partial class ScriptTextEditor : IDisposable
{
    private byte* buffer;
    private int capacity;
    private int bufferLength;
    private long version = -1;
    private readonly List<ColoredSpan> colored = [];
    private readonly List<ScriptDiagnosticMarker> diagnostics = [];
    private string[] lines = [];
    private readonly string label = "##Script" + Guid.NewGuid().ToString("N");
    private string sourcePath = "";
    private bool csharp;
    private CSharpParseOptions? parseOptions;
    private ScriptSemanticResult? appliedSemanticResult;
    private (int Line, int Column)? navigation;
    private int? pendingCursor;
    public bool InputActive { get; private set; }
    internal Func<bool>? CanEditPresentation { get; set; }
    internal bool MayEditSource(ScriptSourceDocument document) => document.CanEditText && CanEditPresentation?.Invoke() == true;
    internal Func<string, string?, bool, bool, bool>? NativeMenuItem { get; set; }

    public void Draw(ScriptSourceDocument document)
    {
        ConfigureNativeReadonlyText();
        EnsureSourceGeometry();
        // External edits and shared undo must rebase selection before clipboard/input commands.
        if (version != document.Version)
        {
            sourcePath = document.Path; csharp = document.IsCSharpSource;
            Load(document.EditorText); version = document.Version;
        }
        DrawSearch(document);
        PrepareSourceCommands(document);
        var id = Gui.GetID(label);
        if (version != document.Version)
        {
            sourcePath = document.Path; csharp = document.IsCSharpSource;
            Load(document.EditorText); version = document.Version;
        }
        var flags = ImGuiInputTextFlags.None;
        if (navigation is { } target)
        {
            pendingSelection = null;
            var line = Math.Clamp(target.Line - 1, 0, lines.Length - 1);
            var column = Math.Clamp(target.Column - 1, 0, lines[line].Length);
            var offset = column;
            for (var index = 0; index < line; index++) offset += lines[index].Length + 1;
            pendingCursor = Encoding.UTF8.GetByteCount(document.EditorText.AsSpan(0, offset));
            focusSource = true; navigation = null;
        }
        var dismissCompletion = PrepareAssistanceInput(document, ref flags);
        sourceOrigin = Gui.GetCursorScreenPos();
        sourceGutterWidth = 22 * Gui.GetStyle().FontScaleDpi + Gui.CalcTextSize(gutterCountText).X + Gui.GetStyle().ItemSpacing.X * 2;
        Gui.SetCursorScreenPos(sourceOrigin + new Vector2(sourceGutterWidth, 0));
        var changed = DrawNativeSource(document);
        if (dismissCompletion)
        {
            Load(document.EditorText); NavigatePosition(caretPosition, document.EditorText);
        }
        if (version != document.Version) { Load(document.EditorText); version = document.Version; }
        FinishAssistanceInput(document, changed && !dismissCompletion);
        DrawColors(id, document);
        UpdateSemanticNavigation(document);
        DrawQuickInfo();
        DrawCodeActionBulb(document);
        DrawAssistance(document);
    }

    public void Navigate(int line, int column) => navigation = (Math.Max(1, line), Math.Max(1, column));

    private void Load(string text)
    {
        sourceVisualCaret = null;
        var rebaseRectangle = false;
        if (geometrySourceText is { } previous && previous != text && pendingSelection == null && pendingCursor == null && navigation == null)
        {
            var prefix = 0;
            while (prefix < previous.Length && prefix < text.Length && previous[prefix] == text[prefix]) prefix++;
            var suffix = 0;
            while (suffix < previous.Length - prefix && suffix < text.Length - prefix && previous[^(suffix + 1)] == text[^(suffix + 1)]) suffix++;
            var change = new Microsoft.CodeAnalysis.Text.TextChange(new Microsoft.CodeAnalysis.Text.TextSpan(prefix, previous.Length - prefix - suffix), text.Substring(prefix, text.Length - prefix - suffix));
            caretPosition = MapSourcePosition(caretPosition, [change], false); sourceAnchor = MapSourcePosition(sourceAnchor, [change], true);
            sourceWordAnchorStart = MapSourcePosition(sourceWordAnchorStart, [change], true);
            sourceWordAnchorEnd = MapSourcePosition(sourceWordAnchorEnd, [change], false);
            rebaseRectangle = rectangle != null; sourceReveal = true;
        }
        var size = Encoding.UTF8.GetByteCount(text);
        EnsureCapacity(size + 1);
        Encoding.UTF8.GetBytes(text.AsSpan(), new Span<byte>(buffer, capacity)); buffer[size] = 0;
        bufferLength = size;
        Highlight(text);
        caretPosition = Math.Clamp(caretPosition, 0, text.Length); sourceAnchor = Math.Clamp(sourceAnchor, 0, text.Length);
        caretPosition = SnapSourcePosition(caretPosition); sourceAnchor = SnapSourcePosition(sourceAnchor);
        if (rebaseRectangle && rectangle is { } box) rectangle = box with { AnchorLine = SourceRow(sourceAnchor), EndLine = SourceRow(caretPosition) };
        caretByte = selectionEndByte = Encoding.UTF8.GetByteCount(text.AsSpan(0, caretPosition));
        selectionStartByte = Encoding.UTF8.GetByteCount(text.AsSpan(0, sourceAnchor));
        selection = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(Math.Min(caretPosition, sourceAnchor), Math.Max(caretPosition, sourceAnchor));
    }
    private void EnsureCapacity(int requested)
    {
        if (capacity >= requested) return;
        var size = Math.Max(requested, Math.Max(4096, checked(capacity * 2)));
        var replacement = (byte*)NativeMemory.Realloc(buffer, (nuint)size);
        if (replacement == null) throw new OutOfMemoryException();
        buffer = replacement; capacity = size;
    }
    private void Highlight(string text)
    {
        colored.Clear(); diagnostics.Clear(); lines = text.Split('\n');
        if (!ReferenceEquals(geometrySourceText, text)) { ClearSourceGeometry(); geometrySourceText = text; }
        gutterCountText = lines.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        assistanceCaret = -1;
        lineStarts = new int[lines.Length];
        for (var index = 1; index < lines.Length; index++) lineStarts[index] = lineStarts[index - 1] + lines[index - 1].Length + 1;
        if (!csharp) return;
        var tree = CSharpSyntaxTree.ParseText(text, options: parseOptions, path: sourcePath);
        var root = tree.GetRoot();
        foreach (var diagnostic in tree.GetDiagnostics())
        {
            if (diagnostic.IsSuppressed || diagnostic.Severity == DiagnosticSeverity.Hidden || !diagnostic.Location.IsInSource) continue;
            var range = diagnostic.Location.GetLineSpan().Span;
            for (var line = range.Start.Line; line <= range.End.Line && line < lines.Length; line++)
            {
                var start = line == range.Start.Line ? range.Start.Character : 0;
                var end = line == range.End.Line ? range.End.Character : lines[line].Length;
                diagnostics.Add(new ScriptDiagnosticMarker(line, start, end, diagnostic.GetMessage(), diagnostic.Severity));
            }
        }
        foreach (var token in root.DescendantTokens())
        {
            uint color = 0;
            if (SyntaxFacts.IsKeywordKind(token.Kind()) || SyntaxFacts.IsContextualKeyword(token.Kind())) color = Rgb(86, 156, 214);
            else if (token.Kind().ToString().Contains("String", StringComparison.Ordinal) || token.IsKind(SyntaxKind.CharacterLiteralToken)) color = Rgb(214, 157, 133);
            else if (token.IsKind(SyntaxKind.NumericLiteralToken)) color = Rgb(181, 206, 168);
            if (color != 0) AddSpan(tree, token.Span, color);
        }
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                AddSpan(tree, trivia.Span, Rgb(87, 166, 74));
            else if (trivia.IsDirective) AddSpan(tree, trivia.Span, Rgb(155, 155, 155));
        }
        colored.Sort((left, right) => left.Line.CompareTo(right.Line));
        sourceColorRevision++;
    }
    private void AddSpan(SyntaxTree tree, Microsoft.CodeAnalysis.Text.TextSpan span, uint color)
    {
        var range = tree.GetLineSpan(span).Span;
        for (var line = range.Start.Line; line <= range.End.Line && line < lines.Length; line++)
        {
            var start = line == range.Start.Line ? range.Start.Character : 0;
            var end = line == range.End.Line ? range.End.Character : lines[line].Length;
            if (end > start) colored.Add(new ColoredSpan(line, start, lines[line][start..end], color));
        }
    }
    public void ApplySemanticResult(ScriptSemanticResult result)
    {
        if (version != result.Source.Version || !string.Equals(sourcePath, result.Source.Path, StringComparison.OrdinalIgnoreCase)) return;
        if (ReferenceEquals(appliedSemanticResult, result)) return;
        appliedSemanticResult = result;
        sourceColorRevision++;
        parseOptions = result.ParseOptions;
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(result.Source.Text);
        colored.Clear(); diagnostics.Clear();
        foreach (var span in result.Colors)
        {
            var range = text.Lines.GetLinePositionSpan(span.Span);
            for (var line = range.Start.Line; line <= range.End.Line && line < lines.Length; line++)
            {
                var start = line == range.Start.Line ? range.Start.Character : 0;
                var end = line == range.End.Line ? range.End.Character : lines[line].Length;
                if (end > start) colored.Add(new ColoredSpan(line, start, lines[line][start..end], span.Color));
            }
        }
        foreach (var issue in result.Issues)
        {
            var range = text.Lines.GetLinePositionSpan(issue.Span);
            for (var line = range.Start.Line; line <= range.End.Line && line < lines.Length; line++)
                diagnostics.Add(new ScriptDiagnosticMarker(line, line == range.Start.Line ? range.Start.Character : 0,
                    line == range.End.Line ? range.End.Character : lines[line].Length, issue.Message, issue.Severity));
        }
        colored.Sort((left, right) => left.Line.CompareTo(right.Line));
    }
    public void InvalidateSemanticResult(string text, bool resetOptions)
    {
        appliedSemanticResult = null;
        if (resetOptions) parseOptions = null;
        if (version >= 0) Highlight(text);
    }
    private void DrawColors(uint id, ScriptSourceDocument document)
    {
        bulbVisible = false;
        var parent = ImGuiP.GetCurrentWindow();
        var children = parent.DC.ChildWindows;
        for (var index = children.Size - 1; index >= 0; index--)
        {
            var child = children[index];
            if (child.ChildId != id) continue;
            var origin = child.Pos + Gui.GetStyle().FramePadding - child.Scroll;
            var clip = child.InnerClipRect;
            var draw = child.DrawList;
            var lineHeight = nativeSourceHeight;
            var caretLine = Array.BinarySearch(lineStarts, caretPosition);
            if (caretLine < 0) caretLine = Math.Max(0, ~caretLine - 1);
            caretLine = Math.Min(caretLine, lines.Length - 1);
            var bulbY = origin.Y + caretLine * lineHeight;
            bulbPosition = new Vector2(sourceOrigin.X + 2 * Gui.GetStyle().FontScaleDpi, bulbY);
            bulbVisible = bulbY >= clip.Min.Y && bulbY + lineHeight <= clip.Max.Y;
            var caretColumn = Math.Clamp(caretPosition - lineStarts[caretLine], 0, lines[caretLine].Length);
            if (assistanceCaret != caretPosition || assistanceFont != Gui.GetFontSize())
            {
                assistanceCaretX = SourceX(caretLine, caretColumn);
                assistanceCaret = caretPosition; assistanceFont = Gui.GetFontSize();
            }
            assistancePosition = new Vector2(Math.Clamp(origin.X + assistanceCaretX, clip.Min.X, Math.Max(clip.Min.X, Gui.GetIO().DisplaySize.X - 520 * Gui.GetStyle().FontScaleDpi)),
                Math.Clamp(origin.Y + (caretLine + 1) * lineHeight, clip.Min.Y, Math.Max(clip.Min.Y, Gui.GetIO().DisplaySize.Y - 220 * Gui.GetStyle().FontScaleDpi)));
            draw.PushClipRect(clip.Min, clip.Max, true);
            DrawSourceLineNumbers(document, child, parent.DrawList, origin, clip.Min, clip.Max, lineHeight);
            DrawSearchHighlights(child, draw, origin, clip.Min, clip.Max, lineHeight);
            var firstLine = Math.Max(0, (int)((clip.Min.Y - origin.Y) / lineHeight) - 1);
            var mouse = Gui.GetMousePos();
            string? tooltip = null;
            foreach (var marker in diagnostics)
            {
                var y = origin.Y + marker.Line * lineHeight;
                if (y + lineHeight < clip.Min.Y || y > clip.Max.Y) continue;
                var ranges = SourceRangeRectangles(marker.Line, marker.Start, marker.End);
                if (ranges.Length == 0) ranges = [(new Vector2(SourceX(marker.Line, marker.Start), 0), new Vector2(SourceX(marker.Line, marker.Start) + MeasureGlyph(" "), lineHeight))];
                var color = marker.Severity == DiagnosticSeverity.Error ? Rgb(255, 0, 0) : marker.Severity == DiagnosticSeverity.Warning ? Rgb(30, 144, 255) : Rgb(50, 205, 50);
                foreach (var range in ranges)
                {
                    var left = origin.X + range.Minimum.X; var right = origin.X + range.Maximum.X;
                    for (var x = Math.Max(left, clip.Min.X); x < Math.Min(right, clip.Max.X); x += 4)
                    {
                        draw.AddLine(new Vector2(x, y + lineHeight - 2), new Vector2(Math.Min(x + 2, right), y + lineHeight), color);
                        draw.AddLine(new Vector2(Math.Min(x + 2, right), y + lineHeight), new Vector2(Math.Min(x + 4, right), y + lineHeight - 2), color);
                    }
                    if (mouse.X >= Math.Max(left, clip.Min.X) && mouse.X <= Math.Min(right, clip.Max.X)
                        && mouse.Y >= Math.Max(y, clip.Min.Y) && mouse.Y < Math.Min(y + lineHeight, clip.Max.Y)) tooltip = marker.Message;
                }
            }
            DrawSemanticNavigation(child, origin, version);
            draw.PopClipRect();
            // The marker tooltip follows the WPF text-marker service's diagnostic message.
            if (tooltip != null && Gui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            {
                Gui.BeginTooltip(); Gui.TextUnformatted(tooltip); Gui.EndTooltip();
            }
            break;
        }
    }
    private static uint Rgb(byte red, byte green, byte blue) => 0xff000000u | (uint)blue << 16 | (uint)green << 8 | red;
    public void Dispose() { NativeMenuItem = null; ReleaseSourceDrop(); ReleaseNativeSourceFocus(); StopSearchWork(); DismissAssistance(); semanticInfoText.Dispose(); searchMessage.Dispose(); ClearSourceGeometry(); nativeGlyphRasterizer?.Dispose(); nativeGlyphRasterizer = null; NativeMemory.Free(buffer); buffer = null; capacity = bufferLength = 0; }
    private sealed record ColoredSpan(int Line, int Column, string Text, uint Color)
    {
        public float X { get; set; }
        public float FontSize { get; set; } = -1;
    }
    private sealed record ScriptDiagnosticMarker(int Line, int Start, int End, string Message, DiagnosticSeverity Severity)
    {
        public float X { get; set; }
        public float Width { get; set; }
        public float FontSize { get; set; } = -1;
    }
}
