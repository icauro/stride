using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>Bottom ticks for the native preview scrubbers, following WPF TickBar.</summary>
internal static partial class PreviewSliderControl
{
    internal static void DrawTicks(double minimum, double maximum, double frequency, bool integer)
    {
        var range = maximum - minimum;
        if (!double.IsFinite(range) || range <= 0 || !double.IsFinite(frequency) || frequency <= 0) return;
        var min = Gui.GetItemRectMin(); var max = Gui.GetItemRectMax();
        var style = Gui.GetStyle();
        // Native preview template thumbs are 10 DIP wide, independent of range.
        var available = max.X - min.X;
        var grab = 10 * style.FontScaleDpi;
        grab = Math.Min(grab, available);
        var start = min.X + grab * .5f;
        var width = available - grab;
        if (width <= 0) return;
        var scale = style.FontScaleDpi;
        var height = Math.Min(4 * scale, max.Y - min.Y);
        var y = max.Y - height;
        var draw = Gui.GetWindowDrawList(); var color = Gui.GetColorU32(ImGuiCol.Text);
        draw.AddLine(new Vector2(start, y), new Vector2(start, max.Y), color);
        draw.AddLine(new Vector2(start + width, y), new Vector2(start + width, max.Y), color);
        // Original TickBar reduces over-dense ticks to one per logical pixel.
        var interval = Math.Max(frequency, range / (width / scale));
        for (var tick = 1; tick * interval < range; ++tick)
        {
            var x = MathF.Round(start + (float)(tick * interval / range) * width) + .5f;
            draw.AddLine(new Vector2(x, y), new Vector2(x, y + height * .75f), color);
        }
    }
}
