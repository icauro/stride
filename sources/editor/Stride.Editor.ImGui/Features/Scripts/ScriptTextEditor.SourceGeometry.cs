using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class ScriptTextEditor
{
    private readonly record struct SourceGlyph(int Start, int End);
    private sealed record SourceLineGeometry(SourceGlyph[] Glyphs, WindowsSourceLine Native) : IDisposable
    {
        internal int UsedFrame;
        internal float Width => Native.Width;
        internal float X(int offset) => Native.Caret(offset).X;
        internal int Hit(float x) => Native.Hit(new(x, Native.Height / 2));
        internal IReadOnlyList<(int Start, int End)> VisualSpans(float left, float right)
        {
            return Native.VisualSpans(left, right);
        }
        public void Dispose() => Native.Dispose();
    }
    private readonly Dictionary<int, SourceLineGeometry> sourceGeometry = [];
    private readonly Dictionary<string, float> glyphWidths = new(StringComparer.Ordinal);
    private static readonly string[] ControlNames = ["NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI", "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US"];
    private static readonly string[] ExtendedControlNames = ["PAD", "HOP", "BPH", "NBH", "IND", "NEL", "SSA", "ESA", "HTS", "HTJ", "VTS", "PLD", "PLU", "RI", "SS2", "SS3", "DCS", "PU1", "PU2", "STS", "CCH", "MW", "SPA", "EPA", "SOS", "SGCI", "SCI", "CSI", "ST", "OSC", "PM", "APC"];
    private float geometryFontSize = -1, geometryDpi = -1, measuredSourceWidth;
    private nint geometryFont;
    private string? geometrySourceText;
    private WindowsTextLayout.GlyphRasterizer? nativeGlyphRasterizer;
    internal WindowsGlyphImages? NativeGlyphImages { get; set; }
    internal Stride.Graphics.CommandList? NativeGlyphCommands { get; set; }
    private uint geometryTextColor, geometryControlColor;
    private long sourceColorRevision, geometryColorRevision = -1;
    private float nativeSourceHeight;
    private void ClearSourceGeometry()
    {
        foreach (var item in sourceGeometry.Values) item.Dispose();
        sourceGeometry.Clear(); measuredSourceWidth = 0;
    }
    private void EnsureSourceGeometry()
    {
        var color = Gui.GetColorU32(ImGuiCol.Text); var disabled = Gui.GetColorU32(ImGuiCol.TextDisabled);
        if (geometryFontSize == Gui.GetFontSize() && geometryDpi == Gui.GetStyle().FontScaleDpi && geometryFont == (nint)Gui.GetFont().Handle
            && geometryTextColor == color && geometryControlColor == disabled && geometryColorRevision == sourceColorRevision) return;
        if (geometryFontSize != Gui.GetFontSize() || geometryDpi != Gui.GetStyle().FontScaleDpi || geometryFont != (nint)Gui.GetFont().Handle) sourceVisualCaret = null;
        geometryFontSize = Gui.GetFontSize(); geometryDpi = Gui.GetStyle().FontScaleDpi; geometryFont = (nint)Gui.GetFont().Handle;
        ClearSourceGeometry(); glyphWidths.Clear();
        geometryTextColor = color; geometryControlColor = disabled; geometryColorRevision = sourceColorRevision;
        nativeGlyphRasterizer ??= new();
        using var metric = new WindowsTextLayout(" ", "Consolas", geometryFontSize / geometryDpi, 1_000_000);
        nativeSourceHeight = metric.Metrics.Height * geometryDpi;
    }
    private float MeasureGlyph(string text)
    {
        if (!glyphWidths.TryGetValue(text, out var width))
        {
            using var metric = new WindowsTextLayout(text, "Consolas", geometryFontSize / geometryDpi, 1_000_000);
            glyphWidths.Add(text, width = metric.Metrics.WidthIncludingTrailingWhitespace * geometryDpi);
        }
        return width;
    }
    private SourceLineGeometry SourceGeometry(int line)
    {
        EnsureSourceGeometry(); line = Math.Clamp(line, 0, lines.Length - 1);
        if (sourceGeometry.TryGetValue(line, out var cached)) { cached.UsedFrame = Gui.GetFrameCount(); return cached; }
        var colors = colored.Where(span => span.Line == line).OrderBy(span => span.Column)
            .Select(span => new WindowsTextLayout.ColorRange(span.Column, span.Text.Length, span.Color)).ToArray();
        cached = BuildSourceGeometry(lines[line], geometryFontSize, geometryDpi, nativeGlyphRasterizer!, colors, geometryTextColor, geometryControlColor);
        cached.UsedFrame = Gui.GetFrameCount(); sourceGeometry.Add(line, cached); measuredSourceWidth = Math.Max(measuredSourceWidth, cached.Width); return cached;
    }
    private static string SourceControlLabel(char c) => c < 32 ? ControlNames[c] : c == 127 ? "DEL" : c is >= (char)128 and <= (char)159 ? ExtendedControlNames[c - 128] : "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
    private static SourceLineGeometry BuildSourceGeometry(string value, float fontSize, float dpi, WindowsTextLayout.GlyphRasterizer rasterizer,
        IReadOnlyList<WindowsTextLayout.ColorRange>? colors = null, uint textColor = 0xffffffff, uint controlColor = 0xff808080)
    {
        var native = new WindowsSourceLine(value, "Consolas", fontSize / dpi, dpi, colors ?? [], SourceControlLabel, rasterizer, textColor, controlColor);
        try
        {
            var glyphs = new List<SourceGlyph>();
            for (var offset = 0; offset < value.Length;)
            {
                var length = StringInfo.GetNextTextElementLength(value.AsSpan(offset));
                glyphs.Add(new(offset, offset + length)); offset += length;
            }
            return new(glyphs.ToArray(), native);
        }
        catch { native.Dispose(); throw; }
    }
    private (int Position, float X)? sourceVisualCaret;
    private float SourceX(int line, int offset) => sourceVisualCaret is { } visual && visual.Position == caretPosition
        && lineStarts[line] + offset == visual.Position ? visual.X : SourceGeometry(line).X(Math.Clamp(offset, 0, lines[Math.Clamp(line, 0, lines.Length - 1)].Length));
    private int SourceHit(int line, float x) => SourceGeometry(line).Hit(Math.Max(0, x));
    private void SetSourcePointCaret(ScriptSourceDocument document, int row, float x, int anchor)
    {
        var hit = SourceGeometry(row).Native.HitCaret(new(Math.Max(0, x), nativeSourceHeight / 2));
        var position = lineStarts[row] + hit.Position;
        SetSourceCaret(document, position, anchor < 0 ? position : anchor); sourceVisualCaret = (position, hit.X);
    }
    private (int Position, float X) VisualSourceMove(int position, bool right)
    {
        var row = SourceRow(position); var x = SourceX(row, position - lineStarts[row]);
        var stops = SourceGeometry(row).Native.VisualCarets();
        var targets = stops.Where(stop => right ? stop.X > x + .01f : stop.X < x - .01f);
        if (targets.Any())
        {
            var target = right ? targets.First() : targets.Last(); return (lineStarts[row] + target.Position, target.X);
        }
        var nextRow = row + (right ? 1 : -1);
        if (nextRow < 0 || nextRow >= lines.Length) return (position, x);
        var nextStops = SourceGeometry(nextRow).Native.VisualCarets(); var edge = right ? nextStops.First() : nextStops.Last();
        return (lineStarts[nextRow] + edge.Position, edge.X);
    }
    private int SourceElementHit(int line, float x)
    {
        return SourceGeometry(line).Native.ElementHit(new(x, nativeSourceHeight / 2));
    }
    private int SnapSourcePosition(int position)
    {
        var row = SourceRow(position); var column = position - lineStarts[row];
        var glyphs = SourceGeometry(row).Glyphs;
        var low = 0; var high = glyphs.Length;
        while (low < high) { var middle = (low + high) / 2; if (glyphs[middle].End <= column) low = middle + 1; else high = middle; }
        return low == glyphs.Length ? position : lineStarts[row] + glyphs[low].Start;
    }
    private int PreviousSourcePosition(int position)
    {
        var line = Array.BinarySearch(lineStarts, position); if (line < 0) line = ~line - 1;
        line = Math.Clamp(line, 0, lines.Length - 1); var column = position - lineStarts[line];
        if (column <= 0) return Math.Max(0, position - 1);
        var glyphs = SourceGeometry(line).Glyphs; var low = 0; var high = glyphs.Length;
        while (low < high) { var middle = (low + high) / 2; if (glyphs[middle].Start < column) low = middle + 1; else high = middle; }
        return lineStarts[line] + glyphs[Math.Max(0, low - 1)].Start;
    }
    private int NextSourcePosition(int position, int length)
    {
        var line = Array.BinarySearch(lineStarts, position); if (line < 0) line = ~line - 1;
        line = Math.Clamp(line, 0, lines.Length - 1); var column = position - lineStarts[line];
        var glyphs = SourceGeometry(line).Glyphs; var low = 0; var high = glyphs.Length;
        while (low < high) { var middle = (low + high) / 2; if (glyphs[middle].End <= column) low = middle + 1; else high = middle; }
        return Math.Min(length, lineStarts[line] + (low < glyphs.Length ? glyphs[low].End : lines[line].Length + 1));
    }
    private void DrawSourceGlyphs(ImDrawListPtr draw, Vector2 origin, Vector2 clipMin, Vector2 clipMax, float height)
    {
        if (NativeGlyphImages == null || NativeGlyphCommands == null)
            throw new InvalidOperationException("The source viewport has no current native glyph graphics owner.");
        var first = Math.Max(0, (int)((clipMin.Y - origin.Y) / height));
        for (var row = first; row < lines.Length && origin.Y + row * height < clipMax.Y; row++)
            SourceGeometry(row).Native.Draw(draw, NativeGlyphCommands, NativeGlyphImages, origin + new Vector2(0, row * height), geometryControlColor, clipMin, clipMax);
        if (sourceGeometry.Count > 256)
            foreach (var pair in sourceGeometry.Where(pair => pair.Value.UsedFrame != Gui.GetFrameCount()).OrderBy(pair => pair.Value.UsedFrame).Take(sourceGeometry.Count - 256).ToArray())
            { pair.Value.Dispose(); sourceGeometry.Remove(pair.Key); }
    }
    private (Vector2 Minimum, Vector2 Maximum)[] SourceRangeRectangles(int row, int start, int end)
        => SourceGeometry(row).Native.Selection(Math.Clamp(start, 0, lines[row].Length), Math.Max(0, Math.Min(end, lines[row].Length) - Math.Clamp(start, 0, lines[row].Length)));
    private void ReleaseNativeSourceFocus()
    {
        if (Gui.GetCurrentContext().Handle == null || sourceSurfaceId == 0 || ImGuiP.GetActiveID() != sourceSurfaceId) return;
        ImGuiP.ClearActiveID();
        var context = Gui.GetCurrentContext(); context.Handle->PlatformImeData.WantVisible = 0; context.Handle->PlatformImeData.WantTextInput = 0;
    }
    internal void SuspendSourcePresentation()
    {
        semanticInfoText.SuspendNativePresentation(); searchMessage.SuspendNativePresentation();
        InputActive = false; sourceHadFocus = false; sourceFocused = false; sourceDragging = false; possibleSourceDrag = false;
        if (sourceDropBinding != null) sourceDropBinding.Transfer = null;
        ReleaseNativeSourceFocus();
    }
    private void ConfigureNativeReadonlyText()
    {
        semanticInfoText.UseNativeShaping = searchMessage.UseNativeShaping = true;
        semanticInfoText.NativeGlyphImages = searchMessage.NativeGlyphImages = NativeGlyphImages;
        semanticInfoText.NativeGlyphCommands = searchMessage.NativeGlyphCommands = NativeGlyphCommands;
    }
}
