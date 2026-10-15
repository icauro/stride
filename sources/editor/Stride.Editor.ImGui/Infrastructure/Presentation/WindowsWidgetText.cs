using System.Globalization;
using System.Numerics;

namespace Stride.Editor.ImGui;

// CPU text layouts are independent of the current graphics-device generation.
internal sealed class WindowsWidgetText : IDisposable
{
    private readonly record struct Key(string Text, string Family, string Locale, float Size, float Dpi, float Width, bool Wrap, uint Weight, uint Color, bool Underline, bool Italic);
    private sealed record Entry(WindowsFormattedText Layout) { internal long Used; }
    private sealed record MetricEntry(Vector2 Size, int Cost) { internal long Used; }
    private readonly Dictionary<Key, Entry> layouts = [];
    private readonly Dictionary<Key, MetricEntry> metrics = [];
    private long metricBytes;
    private WindowsTextLayout.GlyphRasterizer? rasterizer;
    private long clock;
    internal Vector2 Measure(string text, float size, float dpi, uint weight, bool italic = false, float width = 1_000_000, bool wrap = false)
    {
        var key = new Key(text, "Segoe UI", CultureInfo.CurrentUICulture.Name, size, dpi, width, wrap, weight, 0, false, italic);
        if (metrics.TryGetValue(key, out var retained)) { retained.Used = ++clock; return retained.Size; }
        using var layout = new WindowsTextLayout(text, key.Family, size / dpi, key.Width / dpi, wrap: key.Wrap);
        layout.SetStyles(text.Length == 0 ? [] : [new WindowsTextLayout.StyleRange(0, text.Length, size / dpi, weight, Italic: italic)]);
        var actual = new Vector2(layout.Metrics.WidthIncludingTrailingWhitespace, layout.Metrics.Height) * dpi;
        var cost = checked(text.Length * 2 + 192);
        metrics.Add(key, new(actual, cost) { Used = ++clock }); metricBytes += cost;
        while (metrics.Count > 16_384 || metricBytes > 8 * 1024 * 1024)
        {
            var oldest = metrics.MinBy(pair => pair.Value.Used);
            metrics.Remove(oldest.Key); metricBytes -= oldest.Value.Cost;
        }
        return actual;
    }
    internal WindowsFormattedText Get(string text, float size, float dpi, float width, bool wrap, uint weight, uint color, bool underline = false, bool italic = false)
    {
        var key = new Key(text, "Segoe UI", CultureInfo.CurrentUICulture.Name, size, dpi, width, wrap, weight, color, underline, italic);
        if (layouts.TryGetValue(key, out var retained)) { retained.Used = ++clock; return retained.Layout; }
        rasterizer ??= new();
        var layout = new WindowsFormattedText(text, key.Family, size / dpi, width, dpi, wrap,
            text.Length == 0 ? [] : [new WindowsTextLayout.StyleRange(0, text.Length, size / dpi, weight, Italic: italic)], [],
            underline && text.Length != 0 ? [(0, text.Length)] : [], rasterizer, color);
        try
        {
            layouts.Add(key, new(layout) { Used = ++clock });
            if (layouts.Count > 512)
            {
                var oldest = layouts.MinBy(pair => pair.Value.Used);
                layouts.Remove(oldest.Key); oldest.Value.Layout.Dispose();
            }
            return layout;
        }
        catch { layouts.Remove(key); layout.Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var entry in layouts.Values) entry.Layout.Dispose();
        layouts.Clear(); metrics.Clear(); metricBytes = 0; rasterizer?.Dispose(); rasterizer = null;
    }
}
