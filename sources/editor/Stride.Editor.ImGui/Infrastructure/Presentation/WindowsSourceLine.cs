using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Graphics;
using Run = Stride.Editor.ImGui.WindowsGlyphRunImage;

namespace Stride.Editor.ImGui;

/// <summary>Cached original source line: native glyphs, controls and hit geometry share one layout.</summary>
internal sealed class WindowsSourceLine : IDisposable
{
    private sealed record Box(WindowsTextLayout.InlinePlacement Placement, WindowsTextLayout Layout, WindowsTextLayout.GlyphSnapshot Glyphs, Run[] Runs, float LabelBaseline);
    private readonly WindowsTextLayout layout;
    private readonly WindowsTextLayout.GlyphSnapshot glyphs;
    private readonly Run[] runs = [];
    private readonly List<Box> boxes = [];
    private readonly float scale;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool disposed;
    private (int Position, float X)[]? visualCarets;
    private WindowsTextLayout.NativeHitMetrics[]? visualRanges;
    internal string Text => layout.Text;
    internal float Width => layout.Metrics.WidthIncludingTrailingWhitespace * scale;
    internal float Height => layout.Metrics.Height * scale;
    internal float Baseline { get; }

    internal WindowsSourceLine(string text, string family, float fontSizeDip, float pixelsPerDip,
        IReadOnlyList<WindowsTextLayout.ColorRange> colors, Func<char, string> controlLabel,
        WindowsTextLayout.GlyphRasterizer rasterizer, uint defaultColor, uint controlColor)
    {
        if (!float.IsFinite(pixelsPerDip) || pixelsPerDip <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
        scale = pixelsPerDip;
        var labels = new Dictionary<int, WindowsTextLayout>();
        var definitions = new List<WindowsTextLayout.InlineBoxDefinition>();
        WindowsTextLayout? preparedLayout = null; WindowsTextLayout.GlyphSnapshot? preparedGlyphs = null;
        try
        {
            using var metricsLayout = new WindowsTextLayout(" ", family, fontSizeDip, 1_000_000);
            var line = metricsLayout.Lines().First(); Baseline = line.Baseline * scale;
            for (var position = 0; position < text.Length; position++)
            {
                var character = text[position]; if (character == '\t' || !char.IsControl(character)) continue;
                var label = controlLabel(character);
                var labelLayout = new WindowsTextLayout(label, family, fontSizeDip, 1_000_000);
                labels.Add(position, labelLayout);
                definitions.Add(new(position, 1, label, labelLayout.Metrics.WidthIncludingTrailingWhitespace + 6,
                    line.Height, line.Baseline));
            }
            preparedLayout = new(text, family, fontSizeDip, 1_000_000, boxes: definitions, uniformLineHeight: true);
            preparedLayout.SetColors(colors); preparedGlyphs = preparedLayout.Shape();
            runs = Rasterize(preparedGlyphs, rasterizer, defaultColor);
            foreach (var placement in preparedGlyphs.Inlines)
            {
                var labelLayout = labels[placement.Definition.Start]; var labelGlyphs = labelLayout.Shape();
                Run[] labelRuns = [];
                try { labelRuns = Rasterize(labelGlyphs, rasterizer, controlColor); boxes.Add(new(placement, labelLayout, labelGlyphs, labelRuns, labelLayout.Lines().First().Baseline)); }
                catch { foreach (var run in labelRuns) run.Dispose(); labelGlyphs.Dispose(); throw; }
                labels.Remove(placement.Definition.Start);
            }
            layout = preparedLayout; glyphs = preparedGlyphs; preparedLayout = null; preparedGlyphs = null;
        }
        catch
        {
            foreach (var run in runs) run.Dispose();
            foreach (var box in boxes) { foreach (var run in box.Runs) run.Dispose(); box.Glyphs.Dispose(); box.Layout.Dispose(); }
            throw;
        }
        finally
        {
            preparedGlyphs?.Dispose(); preparedLayout?.Dispose();
            foreach (var remaining in labels.Values) remaining.Dispose();
        }
    }
    private Run[] Rasterize(WindowsTextLayout.GlyphSnapshot snapshot, WindowsTextLayout.GlyphRasterizer rasterizer, uint defaultColor)
    {
        var prepared = new List<Run>();
        try
        {
            foreach (var glyph in snapshot.Runs)
            {
                var color = WindowsTextLayout.DrawingColor(glyph.DrawingEffect, defaultColor);
                var run = new Run(rasterizer.Analyze(glyph, scale), color);
                try { prepared.Add(run); } catch { run.Dispose(); throw; }
            }
            return prepared.ToArray();
        }
        catch { foreach (var run in prepared) run.Dispose(); throw; }
    }

    internal Vector2 Caret(int position, bool trailing = false) { CheckOwner(); return layout.Caret(position, trailing).Point * scale; }
    internal int Hit(Vector2 physicalPoint) { CheckOwner(); return layout.HitTest(physicalPoint / scale).Position; }
    internal int ElementHit(Vector2 physicalPoint) { CheckOwner(); return checked((int)layout.HitTest(physicalPoint / scale).Metrics.TextPosition); }
    internal IReadOnlyList<(int Start, int End)> VisualSpans(float left, float right)
    {
        CheckOwner(); visualRanges ??= layout.Selection(0, Text.Length);
        var spans = new List<(int Start, int End)>();
        foreach (var range in visualRanges)
        {
            var minimum = range.Left * scale; var maximum = (range.Left + range.Width) * scale;
            var low = Math.Max(left, minimum); var high = Math.Min(right, maximum);
            if (high <= low) continue;
            var start = checked((int)range.TextPosition); var end = checked(start + (int)range.Length);
            var rtl = (range.BidiLevel & 1) != 0;
            var first = low <= minimum ? rtl ? end : start : Math.Clamp(Hit(new(low, (range.Top + range.Height / 2) * scale)), start, end);
            var last = high >= maximum ? rtl ? start : end : Math.Clamp(Hit(new(high, (range.Top + range.Height / 2) * scale)), start, end);
            if (last != first) spans.Add((Math.Min(first, last), Math.Max(first, last)));
        }
        var merged = new List<(int Start, int End)>();
        foreach (var span in spans.OrderBy(span => span.Start))
            if (merged.Count > 0 && merged[^1].End >= span.Start) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            else merged.Add(span);
        if (merged.Count == 0) { var position = Hit(new(left, Height / 2)); merged.Add((position, position)); }
        return merged;
    }
    internal (int Position, float X) HitCaret(Vector2 physicalPoint)
    {
        CheckOwner(); var hit = layout.HitTest(physicalPoint / scale); var right = (hit.Metrics.BidiLevel & 1) != 0 ? !hit.Trailing : hit.Trailing;
        return (hit.Position, (hit.Metrics.Left + (right ? hit.Metrics.Width : 0)) * scale);
    }
    internal (int Position, float X)[] VisualCarets()
    {
        CheckOwner(); if (visualCarets != null) return visualCarets;
        var stops = new List<(int, float)>(); var position = 0;
        foreach (var cluster in layout.Clusters())
        {
            stops.Add((position, layout.Caret(position).Point.X * scale));
            stops.Add((position + cluster.Length, layout.Caret(position, true).Point.X * scale));
            position += cluster.Length;
        }
        if (stops.Count == 0) stops.Add((0, layout.Caret(0).Point.X * scale));
        return visualCarets = stops.Distinct().OrderBy(stop => stop.Item2).ThenBy(stop => stop.Item1).ToArray();
    }
    internal (Vector2 Minimum, Vector2 Maximum)[] Selection(int start, int length)
    {
        CheckOwner();
        return layout.Selection(start, length).Select(hit =>
            (new Vector2(hit.Left, hit.Top) * scale, new Vector2(hit.Left + hit.Width, hit.Top + hit.Height) * scale)).ToArray();
    }
    internal WindowsTextLayout.NativeClusterMetrics[] Clusters() { CheckOwner(); return layout.Clusters(); }
    internal void Draw(ImDrawListPtr draw, CommandList commands, WindowsGlyphImages images, Vector2 origin, uint controlColor, Vector2 clipMin, Vector2 clipMax)
    {
        CheckOwner();
        foreach (var run in runs) run.Draw(draw, commands, images, origin, clipMin, clipMax);
        foreach (var box in boxes)
        {
            if (box.Placement.Sideways) throw new NotSupportedException("Sideways source control boxes require a vertical source adapter.");
            var definition = box.Placement.Definition; var minimum = origin + box.Placement.Origin * scale;
            if (minimum.X > clipMax.X || minimum.X + definition.Width * scale < clipMin.X || minimum.Y > clipMax.Y || minimum.Y + definition.Height * scale < clipMin.Y) continue;
            // Native inline origin is the box top-left; its retained metrics drive both hit tests and drawing.
            draw.AddRect(minimum + new Vector2(0, scale), minimum + new Vector2(definition.Width * scale, (definition.Height - 1) * scale), controlColor, 2 * scale);
            var labelOrigin = minimum + new Vector2(3 * scale, (definition.Baseline - box.LabelBaseline) * scale);
            foreach (var run in box.Runs) run.Draw(draw, commands, images, labelOrigin, clipMin, clipMax);
        }
    }
    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Native source line geometry belongs to its actual owner thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    public void Dispose()
    {
        if (disposed) return; CheckOwner();
        foreach (var run in runs) run.Dispose();
        foreach (var box in boxes) { foreach (var run in box.Runs) run.Dispose(); box.Glyphs.Dispose(); box.Layout.Dispose(); }
        glyphs.Dispose(); layout.Dispose(); disposed = true;
    }
}
