using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Rendering;

/// <summary>Shared compiled sprite-sheet drawing used by editor previews and thumbnails.</summary>
public static class SpriteSheetPreviewRenderer
{
    private static readonly RectangleF[] ThumbnailRegions =
    {
        new(0, 0, .5f, .5f), new(.5f, 0, .5f, .5f), new(0, .5f, .5f, .5f), new(.5f, .5f, .5f, .5f)
    };
    public static void DrawThumbnail(SpriteBatch batch, SpriteSheet sheet, Vector2 thumbnailSize)
    {
        if (sheet?.Sprites == null || sheet.Sprites.Count == 0) return;
        var imageIndex = 0f;
        foreach (var region in ThumbnailRegions)
        {
            var image = sheet.Sprites[(int)imageIndex]; var frameRegion = image.Region;
            if (image.Texture != null && frameRegion.Width > 0 && frameRegion.Height > 0)
            {
                var origin = new Vector2(frameRegion.Width / 2, frameRegion.Height / 2);
                var ratio = Math.Min(thumbnailSize.X / frameRegion.Width, thumbnailSize.Y / frameRegion.Height);
                var size = new Vector2(frameRegion.Width * ratio, frameRegion.Height * ratio);
                var destination = new RectangleF((thumbnailSize.X - size.X) / 2, (thumbnailSize.Y - size.Y) / 2, size.X * region.Width, size.Y * region.Height);
                if (image.Orientation == ImageOrientation.Rotated90)
                { var x = destination.X; destination.X = destination.Y; destination.Y = x; var width = destination.Width; destination.Width = destination.Height; destination.Height = width; }
                destination.X = region.X * thumbnailSize.X + region.Width * destination.X + .5f * destination.Width;
                destination.Y = region.Y * thumbnailSize.Y + region.Height * destination.Y + .5f * destination.Height;
                batch.Draw(image.Texture, destination, frameRegion, Color.White, 0, origin, SpriteEffects.None, image.Orientation);
            }
            imageIndex = Math.Min(sheet.Sprites.Count - 1, imageIndex + sheet.Sprites.Count / (float)ThumbnailRegions.Length);
        }
    }
    public static void DrawFrame(SpriteBatch batch, Texture texture, RectangleF region, ImageOrientation orientation,
        Vector2 windowSize, Vector2 offsets, float scale)
    {
        if (texture == null) return;
        batch.Draw(texture, windowSize / 2, region, Color.White, 0, new Vector2(region.Width / 2, region.Height / 2) - offsets,
            scale * Vector2.One, SpriteEffects.None, orientation);
    }
}
