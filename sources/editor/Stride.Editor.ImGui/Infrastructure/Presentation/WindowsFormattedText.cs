using System.Numerics;
using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>Original text, wrapped native font/style layout and lazily tiled glyphs.</summary>
internal sealed class WindowsFormattedText : IDisposable
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly WindowsTextLayout layout;
    private readonly WindowsTextLayout.GlyphSnapshot glyphs;
    private readonly WindowsGlyphRunImage[] images;
    private readonly float scale;
    private readonly uint defaultColor;
    private bool disposed;
    private readonly record struct VisualLine(int Start, int Length, float Top, float Height);
    private readonly VisualLine[] lines;
    private readonly Dictionary<int, (int Position, Vector2 Point, float Height)[]> lineStops = [];
    internal string Text => layout.Text;
    internal float Width => layout.Metrics.WidthIncludingTrailingWhitespace * scale;
    internal float Height => layout.Metrics.Height * scale;
    internal Vector2 Size => new(Width, Height);
    internal float HeightForLines(int count) { CheckOwner(); return lines.Take(Math.Max(0, count)).Sum(line => line.Height); }
    internal WindowsFormattedText(string text, string family, float sizeDip, float physicalWidth, float pixelsPerDip, bool wrap,
        IReadOnlyList<WindowsTextLayout.StyleRange> styles, IReadOnlyList<WindowsTextLayout.ColorRange> colors,
        IReadOnlyList<(int Start, int Length)> underlines, WindowsTextLayout.GlyphRasterizer rasterizer, uint foreground)
    {
        scale = pixelsPerDip; defaultColor = foreground;
        layout = new(text, family, sizeDip, physicalWidth / pixelsPerDip, wrap);
        WindowsTextLayout.GlyphSnapshot? snapshot = null; var prepared = new List<WindowsGlyphRunImage>();
        try
        {
            layout.SetStyles(styles); layout.SetColors(colors); layout.SetUnderlines(underlines); snapshot = layout.Shape();
            foreach (var glyph in snapshot.Runs)
            {
                var image = new WindowsGlyphRunImage(rasterizer.Analyze(glyph, pixelsPerDip), WindowsTextLayout.DrawingColor(glyph.DrawingEffect, foreground));
                try { prepared.Add(image); } catch { image.Dispose(); throw; }
            }
            var sourcePosition = 0; var y = 0f; var measuredLines = new List<VisualLine>();
            foreach (var line in layout.Lines())
            {
                measuredLines.Add(new(sourcePosition, checked((int)line.Length), y, line.Height * scale));
                sourcePosition += checked((int)line.Length); y += line.Height * scale;
            }
            lines = measuredLines.ToArray(); images = prepared.ToArray(); glyphs = snapshot; snapshot = null;
        }
        catch { foreach (var image in prepared) image.Dispose(); snapshot?.Dispose(); layout.Dispose(); throw; }
    }
    internal (int Position, Vector2 Point, float Height) Hit(Vector2 point)
    {
        CheckOwner(); var hit = layout.HitTest(point / scale); var right = (hit.Metrics.BidiLevel & 1) != 0 ? !hit.Trailing : hit.Trailing;
        return (hit.Position, new Vector2(hit.Metrics.Left + (right ? hit.Metrics.Width : 0), hit.Metrics.Top) * scale, hit.Metrics.Height * scale);
    }
    internal (Vector2 Point, float Height) Caret(int position)
    { CheckOwner(); var caret = layout.Caret(position); return (caret.Point * scale, caret.Metrics.Height * scale); }
    internal (int Position, Vector2 Point, float Height) VisualMove(int position, Vector2 point, bool right)
    {
        CheckOwner(); var low = 0; var high = lines.Length;
        while (low < high) { var middle = low + (high - low) / 2; if (lines[middle].Top <= point.Y + .01f) low = middle + 1; else high = middle; }
        var row = Math.Clamp(low - 1, 0, Math.Max(0, lines.Length - 1));
        if (lines.Length == 0) { var current = Caret(position); return (position, current.Point, current.Height); }
        var stops = Stops(row); var candidates = stops.Where(stop => right ? stop.Point.X > point.X + .01f : stop.Point.X < point.X - .01f);
        if (candidates.Any()) return right ? candidates.First() : candidates.Last();
        var nextRow = row + (right ? 1 : -1);
        if (nextRow < 0 || nextRow >= lines.Length) { var current = Caret(position); return (position, point, current.Height); }
        var nextStops = Stops(nextRow); return right ? nextStops.First() : nextStops.Last();
    }
    private (int Position, Vector2 Point, float Height)[] Stops(int row)
    {
        if (lineStops.TryGetValue(row, out var known)) return known;
        var line = lines[row]; var end = Math.Min(Text.Length, line.Start + line.Length); var stops = new List<(int, Vector2, float)>();
        for (var position = line.Start; position < end;)
        {
            var first = layout.Caret(position); var last = layout.Caret(position, true);
            if (Math.Abs(first.Point.Y * scale - line.Top) < .1f) stops.Add((position, first.Point * scale, first.Metrics.Height * scale));
            if (Math.Abs(last.Point.Y * scale - line.Top) < .1f) stops.Add((Math.Min(Text.Length, checked((int)(last.Metrics.TextPosition + last.Metrics.Length))), last.Point * scale, last.Metrics.Height * scale));
            position += StringInfo.GetNextTextElementLength(Text.AsSpan(position, end - position));
        }
        if (stops.Count == 0) { var caret = Caret(line.Start); stops.Add((line.Start, caret.Point, caret.Height)); }
        var result = stops.Distinct().OrderBy(stop => stop.Item2.X).ThenBy(stop => stop.Item1).ToArray();
        if (lineStops.Count >= 128) lineStops.Remove(lineStops.Keys.First());
        lineStops.Add(row, result); return result;
    }
    internal (Vector2 Minimum, Vector2 Maximum)[] Selection(int start, int length)
    {
        CheckOwner(); return layout.Selection(start, length).Select(range =>
            (new Vector2(range.Left, range.Top) * scale, new Vector2(range.Left + range.Width, range.Top + range.Height) * scale)).ToArray();
    }
    internal void Draw(ImDrawListPtr draw, CommandList commands, WindowsGlyphImages owner, Vector2 origin, Vector2 clipMin, Vector2 clipMax)
    {
        CheckOwner(); foreach (var image in images) image.Draw(draw, commands, owner, origin, clipMin, clipMax);
        foreach (var decoration in glyphs.Decorations)
        {
            if (decoration.ReadingDirection > 1 || decoration.FlowDirection > 1) throw new NotSupportedException("Vertical native text decoration is not configured.");
            var minimum = origin + (decoration.Baseline + new Vector2(0, decoration.Offset)) * scale;
            var maximum = minimum + new Vector2(decoration.Width, decoration.Thickness) * scale;
            if (maximum.Y < clipMin.Y || minimum.Y > clipMax.Y || maximum.X < clipMin.X || minimum.X > clipMax.X) continue;
            draw.AddRectFilled(minimum, maximum, WindowsTextLayout.DrawingColor(decoration.DrawingEffect, defaultColor));
        }
    }
    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Native formatted text belongs to its actual owner thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    public void Dispose()
    {
        if (disposed) return; CheckOwner(); foreach (var image in images) image.Dispose(); glyphs.Dispose(); layout.Dispose(); disposed = true;
    }
}
