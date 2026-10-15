using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static NVector2 SpriteCanvasThumbOrigin(SpriteSettingFrame frame, NVector2 imageOrigin, float scale, float dpi, SpriteCanvasDrag drag)
    {
        var region = frame.Value.TextureRegion;
        var minimum = imageOrigin + new NVector2(region.X, region.Y) * scale - new NVector2(6 * dpi);
        var maximum = imageOrigin + new NVector2(region.Right, region.Bottom) * scale + new NVector2(6 * dpi);
        if (drag == SpriteCanvasDrag.Pivot)
        {
            var center = frame.Value.Center + (frame.Value.CenterFromMiddle
                ? new Stride.Core.Mathematics.Vector2(region.Width, region.Height) / 2 : new Stride.Core.Mathematics.Vector2(.5f));
            return imageOrigin + new NVector2(region.X + center.X, region.Y + center.Y) * scale - new NVector2(8 * dpi);
        }
        if (drag == SpriteCanvasDrag.Move) return minimum + new NVector2(10 * dpi);
        if ((int)drag >= (int)SpriteCanvasDrag.BorderLeft)
        {
            var borders = frame.Value.Borders;
            minimum += new NVector2(borders.X, borders.Y) * scale;
            maximum -= new NVector2(borders.Z, borders.W) * scale;
        }
        var left = drag.HasFlag(SpriteCanvasDrag.Left) || drag.HasFlag(SpriteCanvasDrag.BorderLeft);
        var right = drag.HasFlag(SpriteCanvasDrag.Right) || drag.HasFlag(SpriteCanvasDrag.BorderRight);
        var top = drag.HasFlag(SpriteCanvasDrag.Top) || drag.HasFlag(SpriteCanvasDrag.BorderTop);
        var bottom = drag.HasFlag(SpriteCanvasDrag.Bottom) || drag.HasFlag(SpriteCanvasDrag.BorderBottom);
        return new(right ? maximum.X - 10 * dpi : left ? minimum.X : minimum.X + 10 * dpi,
            bottom ? maximum.Y - 10 * dpi : top ? minimum.Y : minimum.Y + 10 * dpi);
    }
    private static string SpriteCanvasEditTitle(SpriteCanvasDrag drag)
    {
        if (drag == SpriteCanvasDrag.Pivot) return "Move sprite center";
        if (drag == SpriteCanvasDrag.Move) return "Move region";
        if ((int)drag < (int)SpriteCanvasDrag.BorderLeft) return "Update region size";
        var horizontal = drag.HasFlag(SpriteCanvasDrag.BorderLeft) ? "Left" : drag.HasFlag(SpriteCanvasDrag.BorderRight) ? "Right" : "";
        var vertical = drag.HasFlag(SpriteCanvasDrag.BorderTop) ? "Top" : drag.HasFlag(SpriteCanvasDrag.BorderBottom) ? "Bottom" : "";
        return horizontal.Length > 0 && vertical.Length > 0 ? $"Update sprite {vertical}{horizontal} borders."
            : $"Update sprite {vertical}{horizontal} border";
    }
    private static SpriteCanvasDrag HitSpriteCanvasHandles(NVector2 pointer, NVector2 minimum, NVector2 maximum, float dpi, bool borders)
    {
        var width = 10 * dpi;
        bool Inside(NVector2 from, NVector2 to) => pointer.X >= from.X && pointer.Y >= from.Y && pointer.X <= to.X && pointer.Y <= to.Y;
        if (!Inside(minimum, maximum)) return SpriteCanvasDrag.None;
        var left = borders ? SpriteCanvasDrag.BorderLeft : SpriteCanvasDrag.Left;
        var right = borders ? SpriteCanvasDrag.BorderRight : SpriteCanvasDrag.Right;
        var top = borders ? SpriteCanvasDrag.BorderTop : SpriteCanvasDrag.Top;
        var bottom = borders ? SpriteCanvasDrag.BorderBottom : SpriteCanvasDrag.Bottom;
        // The original transparent corner thumbs are 10 DIP square. The four
        // edge thumbs stretch between them, so the whole edge remains draggable.
        // Preserve native child z-order when zero/small regions overlap thumbs.
        if (Inside(maximum - new NVector2(width), maximum)) return right | bottom;
        if (Inside(new(minimum.X + width, maximum.Y - width), new(maximum.X - width, maximum.Y))) return bottom;
        if (Inside(new(minimum.X, maximum.Y - width), new(minimum.X + width, maximum.Y))) return left | bottom;
        if (Inside(new(maximum.X - width, minimum.Y + width), new(maximum.X, maximum.Y - width))) return right;
        if (Inside(new(minimum.X, minimum.Y + width), new(minimum.X + width, maximum.Y - width))) return left;
        if (Inside(new(maximum.X - width, minimum.Y), new(maximum.X, minimum.Y + width))) return right | top;
        if (Inside(new(minimum.X + width, minimum.Y), new(maximum.X - width, minimum.Y + width))) return top;
        if (Inside(minimum, minimum + new NVector2(width))) return left | top;
        return !borders && Inside(minimum + new NVector2(width), maximum - new NVector2(width)) ? SpriteCanvasDrag.Move : SpriteCanvasDrag.None;
    }
    private static void DrawSpriteBorderRectangle(NVector2 minimum, NVector2 maximum, uint color, float dpi, NVector2 clipMinimum, NVector2 clipMaximum)
    {
        var draw = Gui.GetWindowDrawList(); var dash = 8 * dpi;
        var startX = minimum.X + Math.Max(0, MathF.Floor((clipMinimum.X - minimum.X) / (dash * 2))) * dash * 2;
        for (var x = startX; x < Math.Min(maximum.X, clipMaximum.X); x += dash * 2)
        {
            var end = Math.Min(x + dash, maximum.X);
            draw.AddLine(new(x, minimum.Y), new(end, minimum.Y), color, 2 * dpi);
            draw.AddLine(new(x, maximum.Y), new(end, maximum.Y), color, 2 * dpi);
        }
        var startY = minimum.Y + Math.Max(0, MathF.Floor((clipMinimum.Y - minimum.Y) / (dash * 2))) * dash * 2;
        for (var y = startY; y < Math.Min(maximum.Y, clipMaximum.Y); y += dash * 2)
        {
            var end = Math.Min(y + dash, maximum.Y);
            draw.AddLine(new(minimum.X, y), new(minimum.X, end), color, 2 * dpi);
            draw.AddLine(new(maximum.X, y), new(maximum.X, end), color, 2 * dpi);
        }
    }
    private static void SetSpriteCanvasDragCursor(SpriteCanvasDrag drag)
    {
        if (drag == SpriteCanvasDrag.None) return;
        var horizontal = drag.HasFlag(SpriteCanvasDrag.Left) || drag.HasFlag(SpriteCanvasDrag.Right)
            || drag.HasFlag(SpriteCanvasDrag.BorderLeft) || drag.HasFlag(SpriteCanvasDrag.BorderRight);
        var vertical = drag.HasFlag(SpriteCanvasDrag.Top) || drag.HasFlag(SpriteCanvasDrag.Bottom)
            || drag.HasFlag(SpriteCanvasDrag.BorderTop) || drag.HasFlag(SpriteCanvasDrag.BorderBottom);
        var ascending = drag.HasFlag(SpriteCanvasDrag.Left) && drag.HasFlag(SpriteCanvasDrag.Top)
            || drag.HasFlag(SpriteCanvasDrag.Right) && drag.HasFlag(SpriteCanvasDrag.Bottom)
            || drag.HasFlag(SpriteCanvasDrag.BorderLeft) && drag.HasFlag(SpriteCanvasDrag.BorderTop)
            || drag.HasFlag(SpriteCanvasDrag.BorderRight) && drag.HasFlag(SpriteCanvasDrag.BorderBottom);
        Gui.SetMouseCursor(horizontal && vertical ? ascending ? ImGuiMouseCursor.ResizeNwse : ImGuiMouseCursor.ResizeNesw
            : horizontal ? ImGuiMouseCursor.ResizeEw : vertical ? ImGuiMouseCursor.ResizeNs : ImGuiMouseCursor.ResizeAll);
    }
}
