using Stride.Graphics;
using Stride.Core.Mathematics;
using Stride.TextureConverter;

namespace Stride.Editor.ImGui;

internal sealed class SpriteSourceImage : IDisposable
{
    private readonly TexImage pixels;
    private Image? upload;
    private int references = 1;
    private readonly object lifetime = new();
    private bool destroying, disposed, pixelsReleased;
    private SpriteSourceImage(TexImage pixels, Image upload) { this.pixels = pixels; this.upload = upload; }
    public Image Upload => upload ?? throw new ObjectDisposedException(nameof(SpriteSourceImage));
    public void ReleaseUpload() { lock (lifetime) { upload?.Dispose(); upload = null; } }
    public SpriteSourceImage Retain()
    {
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(destroying || disposed, this);
            references = checked(references + 1); return this;
        }
    }
    public void Dispose()
    {
        lock (lifetime)
        {
            if (disposed) return;
            if (references > 1) { references--; return; }
            destroying = true;
            ReleaseUpload();
            if (!pixelsReleased) { pixels.Dispose(); pixelsReleased = true; }
            references = 0; disposed = true;
        }
    }
    public Color PickColor(Int2 point) { using var tool = new TextureTool(); return tool.PickColor(pixels, point); }
    public Rectangle FindRegion(Int2 point, bool transparency, Color key)
    { using var tool = new TextureTool(); return transparency ? tool.FindSpriteRegion(pixels, point) : tool.FindSpriteRegion(pixels, point, key, 0x00ffffff); }
    public static SpriteSourceImage Load(string source)
    {
        // Keep the WPF SpriteEditorImageCache decoding/decompression semantics.
        using var tool = new TextureTool();
        var image = tool.Load(source, false);
        try
        {
            tool.Decompress(image, image.Format.IsSRgb);
            if (image.Format == PixelFormat.R16G16B16A16_UNorm) tool.Convert(image, PixelFormat.R8G8B8A8_UNorm);
            return new SpriteSourceImage(image, tool.ConvertToStrideImage(image));
        }
        catch { image.Dispose(); throw; }
    }
}
