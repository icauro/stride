using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>Explicit, synchronous test-only GPU readback. Invoke on the render thread after the tested draw.</summary>
internal sealed class EngineOutputSample
{
    private readonly byte[] pixels;
    internal int Width { get; }
    internal int Height { get; }
    internal PixelFormat Format { get; }

    private EngineOutputSample(Texture target, byte[] pixels)
    { Width = target.Width; Height = target.Height; Format = target.Format; this.pixels = pixels; }

    internal static EngineOutputSample Read(CommandList commands, Texture target)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Format is not (PixelFormat.R8G8B8A8_UNorm or PixelFormat.R8G8B8A8_UNorm_SRgb
            or PixelFormat.B8G8R8A8_UNorm or PixelFormat.B8G8R8A8_UNorm_SRgb))
            throw new NotSupportedException($"Read the actual 8-bit presentation target, not {target.Format}.");
        var pixels = target.GetData<byte>(commands);
        if (pixels.Length != checked(target.Width * target.Height * 4))
            throw new InvalidOperationException("Engine output readback did not return one complete RGBA/BGRA image.");
        return new(target, pixels);
    }

    /// <summary>Compare RGB values in a fixture-defined region; ignore alpha and small quantization differences.</summary>
    internal EngineOutputDifference Compare(EngineOutputSample other, int x, int y, int width, int height, int channelTolerance = 3)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Width != other.Width || Height != other.Height || Format != other.Format)
            throw new InvalidOperationException("Engine output comparison requires unchanged dimensions and format.");
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || width > Width - x || height > Height - y)
            throw new ArgumentOutOfRangeException(nameof(width), "The comparison region must fit inside the actual target.");
        if (channelTolerance is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(channelTolerance));
        var changed = 0; long absoluteDelta = 0;
        for (var row = y; row < y + height; row++)
        for (var column = x; column < x + width; column++)
        {
            var offset = (row * Width + column) * 4;
            var pixelChanged = false;
            for (var channel = 0; channel < 3; channel++)
            {
                var delta = Math.Abs(pixels[offset + channel] - other.pixels[offset + channel]);
                absoluteDelta += delta;
                pixelChanged |= delta > channelTolerance;
            }
            if (pixelChanged) changed++;
        }
        return new(width * height, changed, absoluteDelta);
    }
}

internal readonly record struct EngineOutputDifference(int Pixels, int ChangedPixels, long AbsoluteRgbDelta)
{
    internal double ChangedFraction => (double)ChangedPixels / Pixels;
}
