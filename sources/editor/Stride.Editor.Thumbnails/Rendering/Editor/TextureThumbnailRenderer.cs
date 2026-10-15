using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Rendering;

/// <summary>Shared background pass used by engine-rendered texture thumbnails.</summary>
public static class TextureThumbnailRenderer
{
    /// <summary>The caller owns the sprite batch Begin/End and source texture lifetime.</summary>
    public static void DrawBackground(SpriteBatch batch, Texture texture, Vector2 size, Color4 color,
        Color4 additiveColor = default, SwizzleMode swizzle = SwizzleMode.None)
        => batch.Draw(texture, new RectangleF(0, 0, size.X, size.Y), null, color, 0, Vector2.Zero,
            SpriteEffects.None, ImageOrientation.AsIs, 0, additiveColor, swizzle);
}
