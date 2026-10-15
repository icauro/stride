using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

/// <summary>The original horizontal hue strip, downward brightness surface and checker preview.</summary>
internal static class NativeColorPickerSurface
{
    internal static void Draw(ref ColorHSV color, ref bool accepted, float scale, System.Func<bool> begin, out bool active, out bool commit)
    {
        var value = color; active = commit = false;
        var width = 160 * scale; var strip = 22 * scale; var draw = Gui.GetWindowDrawList();
        Gui.BeginGroup();
        try
        {
            var start = Gui.GetCursorScreenPos();
            Gui.InvisibleButton("##Hue", new Vector2(width, strip));
            var hueActive = Gui.IsItemActive(); var hueCommit = Gui.IsItemDeactivated() && accepted;
            if (Gui.IsItemActivated()) accepted = begin();
            var hueAccepted = accepted;
            if (hueActive && hueAccepted)
            {
                value.H = 360 * Math.Clamp((Gui.GetIO().MousePos.X - start.X) / width, 0, 1);
            }
            active |= hueActive && hueAccepted; commit |= hueCommit;
            float[] stops = [0, 1f / 6, 2f / 6, .5f, 4f / 6, .88888888883f, 1];
            Vector4[] colors = [new(1, 0, 0, 1), new(1, 1, 0, 1), new(0, 1, 0, 1), new(0, 1, 1, 1), new(0, 0, 1, 1), new(1, 0, 1, 1), new(1, 0, 0, 1)];
            for (var index = 0; index < stops.Length - 1; index++)
                draw.AddRectFilledMultiColor(start + new Vector2(width * stops[index], 0), start + new Vector2(width * stops[index + 1], strip),
                    Gui.GetColorU32(colors[index]), Gui.GetColorU32(colors[index + 1]), Gui.GetColorU32(colors[index + 1]), Gui.GetColorU32(colors[index]));
            var hueX = start.X + width * value.H / 360;
            draw.AddRectFilledMultiColor(new Vector2(hueX, start.Y), new Vector2(hueX + 2 * scale, start.Y + strip), 0xff000000, 0xff000000, 0xffffffff, 0xffffffff);
            Gui.SetCursorScreenPos(start + new Vector2(2 * scale, strip + 4 * scale));
            var surface = Gui.GetCursorScreenPos();
            Gui.InvisibleButton("##SaturationBrightness", new Vector2(width, width));
            var surfaceActive = Gui.IsItemActive(); var surfaceCommit = Gui.IsItemDeactivated() && accepted;
            if (Gui.IsItemActivated()) accepted = begin();
            var surfaceAccepted = accepted;
            if (surfaceActive && surfaceAccepted)
            {
                var point = (Gui.GetIO().MousePos - surface) / width;
                value.S = Math.Clamp(point.X, 0, 1); value.V = Math.Clamp(point.Y, 0, 1);
            }
            active |= surfaceActive && surfaceAccepted; commit |= surfaceCommit;
            var saturated = new Color(new ColorHSV(value.H, 1, 1, 1).ToColor()).ToColor4();
            draw.AddRectFilledMultiColor(surface, surface + new Vector2(width), 0xff000000, 0xff000000,
                Gui.GetColorU32(new Vector4(saturated.R, saturated.G, saturated.B, 1)), 0xffffffff);
            var center = surface + new Vector2(value.S, value.V) * width;
            for (var segment = 0; segment < 32; segment++)
            {
                var angle = segment * MathF.Tau / 32; var next = (segment + 1) * MathF.Tau / 32;
                var midpoint = (angle + next) * .5f;
                var gray = Math.Clamp(.5f - (MathF.Cos(midpoint) + MathF.Sin(midpoint)) * .25f, 0, 1);
                draw.AddLine(center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (5 * scale),
                    center + new Vector2(MathF.Cos(next), MathF.Sin(next)) * (5 * scale), Gui.GetColorU32(new Vector4(gray, gray, gray, 1)), 2 * scale);
            }
            var previewStart = surface + new Vector2(0, width + 4 * scale); var previewSize = new Vector2(width, 21 * scale);
            Gui.SetCursorScreenPos(previewStart); Gui.Dummy(previewSize);
            var tile = 7 * scale;
            for (var y = 0f; y < previewSize.Y; y += tile)
                for (var x = 0f; x < previewSize.X; x += tile)
                    draw.AddRectFilled(previewStart + new Vector2(x, y), previewStart + new Vector2(Math.Min(x + tile, previewSize.X), Math.Min(y + tile, previewSize.Y)),
                        ((int)(x / tile) + (int)(y / tile)) % 2 == 0 ? 0xff000000 : 0xffffffff);
            var preview = new Color(value.ToColor().ToArray()).ToColor4();
            draw.AddRectFilled(previewStart, previewStart + previewSize, Gui.GetColorU32(new Vector4(preview.R, preview.G, preview.B, preview.A)));
        }
        finally { Gui.EndGroup(); }
        color = value;
    }
}
