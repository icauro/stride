using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed class WindowsGlyphRunImage(WindowsTextLayout.GlyphRasterizer.Analysis analysis, uint color) : IDisposable
{
    internal readonly WindowsTextLayout.GlyphRasterizer.Analysis Analysis = analysis;
    internal readonly uint Color = color;
    private sealed class Tile(WindowsTextLayout.GlyphRasterizer.Bitmap bitmap) { internal readonly WindowsTextLayout.GlyphRasterizer.Bitmap Bitmap = bitmap; internal long Used; }
    private readonly Dictionary<(int X, int Y), Tile> tiles = [];
    private long drawn;
    internal void Draw(ImDrawListPtr draw, CommandList commands, WindowsGlyphImages images, Vector2 origin, Vector2 clipMin, Vector2 clipMax)
    {
        const int width = 1024, height = 128;
        var bounds = Analysis.Bounds;
        var left = Math.Max(bounds.Left, (int)MathF.Floor(clipMin.X - origin.X)); var top = Math.Max(bounds.Top, (int)MathF.Floor(clipMin.Y - origin.Y));
        var right = Math.Min(bounds.Right, (int)MathF.Ceiling(clipMax.X - origin.X)); var bottom = Math.Min(bounds.Bottom, (int)MathF.Ceiling(clipMax.Y - origin.Y));
        if (right <= left || bottom <= top) return;
        var current = ++drawn;
        for (var y = (int)MathF.Floor((float)top / height) * height; y < bottom; y += height)
            for (var x = (int)MathF.Floor((float)left / width) * width; x < right; x += width)
            {
                if (!tiles.TryGetValue((x, y), out var tile)) tiles.Add((x, y), tile = new(Analysis.Rasterize(x, y, checked(x + width), checked(y + height))));
                tile.Used = current;
                images.Draw(draw, commands, tile.Bitmap, origin, Color);
            }
        // GPU ownership is separate; its actual submitted-work marker retains old images.
        if (tiles.Count > 8) foreach (var pair in tiles.Where(pair => pair.Value.Used != current).ToArray()) tiles.Remove(pair.Key);
    }
    public void Dispose() { Analysis.Dispose(); tiles.Clear(); }
}
