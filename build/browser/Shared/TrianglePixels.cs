using System;

internal static class TrianglePixels
{
    public static (int Foreground, int Background) Verify(int length, int width, int height, Func<int, int> pixel)
    {
        if (length != width * height * 4)
            throw new InvalidOperationException($"Unexpected readback length: {length}");
        int foreground = 0, background = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            bool red = pixel(offset) == 255 && pixel(offset + 1) == 0 && pixel(offset + 2) == 0 && pixel(offset + 3) == 255;
            bool blue = pixel(offset) == 0 && pixel(offset + 1) == 0 && pixel(offset + 2) == 255 && pixel(offset + 3) == 255;
            if (!red && !blue) throw new InvalidOperationException($"Unexpected RGBA at ({x}, {y})");
            if (red) foreground++;
            else background++;
            // Ignore only a narrow edge band when testing analytic coverage.
            double ndcX = 2.0 * (x + 0.5) / width - 1.0;
            double ndcY = 1.0 - 2.0 * (y + 0.5) / height;
            double edge = 0.4 - 0.5 * ndcY - Math.Abs(ndcX);
            if (ndcY > -0.7 && ndcY < 0.7 && edge > 0.08 && !red)
                throw new InvalidOperationException($"Missing triangle pixel ({x}, {y})");
            if ((ndcY < -0.85 || ndcY > 0.85 || edge < -0.08) && !blue)
                throw new InvalidOperationException($"Unexpected foreground ({x}, {y})");
        }
        if (foreground < 500 || background < 500)
            throw new InvalidOperationException("Expected substantial foreground and background.");
        return (foreground, background);
    }
}
