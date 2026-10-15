using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Rendering;

/// <summary>Real glyph drawing shared by the WPF and ImGui font presentation paths.</summary>
public static class SpriteFontPreviewRenderer
{
    public static void DrawThumbnail(SpriteBatch batch, SpriteFont font, string title, Vector2 targetSize, Color color, float fill = .75f)
    {
        if (font == null) return;
        var textSize = font.MeasureString(title);
        if (textSize.X <= 0 || textSize.Y <= 0) return;
        var scale = fill * Math.Min(targetSize.X / textSize.X, targetSize.Y / textSize.Y);
        var size = scale * font.Size;
        if (font.FontType == SpriteFontType.Dynamic)
        {
            scale = 1;
            textSize = font.MeasureString(title, size);
            font.PreGenerateGlyphs(title, new Vector2(size, size));
        }
        batch.DrawString(font, title, size, targetSize / 2, color, 0, textSize / 2, scale * Vector2.One, SpriteEffects.None, 1, TextAlignment.Center);
    }
    public static void DrawPreview(SpriteBatch batch, SpriteFont font, string text, Vector2 windowSize, Vector2 offsets)
    {
        if (font == null) return;
        var displayed = string.IsNullOrEmpty(text) ? "Enter the text to preview" : text;
        batch.DrawString(font, displayed, offsets + (windowSize - font.MeasureString(displayed)) / 2, Color.White);
    }
    public static string SplitFontName(string name, int maximumCharacters = 10)
    {
        var parts = name.Split(' ');
        if (parts.Length <= 1) return name;
        var line = parts[0]; var result = line;
        for (var index = 1; index < parts.Length; index++)
        {
            var next = line + " " + parts[index];
            if (next.Length > maximumCharacters) { result += "\n" + parts[index]; line = parts[index]; }
            else { result += " " + parts[index]; line = next; }
        }
        return result;
    }
}
