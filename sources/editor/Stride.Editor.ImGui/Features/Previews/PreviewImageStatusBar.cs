using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

/// <summary>The original preview Grid's star columns and native StatusBar container metrics.</summary>
internal static class PreviewImageStatusBar
{
    // Native StatusBarItem.Padding=3; preview TextBlock.Margin=5.
    internal static float Height => Gui.GetFontSize() + 16 * Gui.GetStyle().FontScaleDpi;

    internal static void Draw(float scale, int width, int otherDimension, bool heightmap)
    {
        var origin = Gui.GetCursorScreenPos();
        var availableWidth = Math.Max(0, Gui.GetContentRegionAvail().X);
        var dpi = Gui.GetStyle().FontScaleDpi;
        var height = Height;
        // StatusBar.SeparatorStyleKey: MinWidth=1, Margin=2 on both sides.
        var separatorWidth = 5 * dpi;
        var firstColumn = Math.Max(0, availableWidth - separatorWidth) / 3;
        var separatorX = origin.X + firstColumn;
        var dimensionsX = separatorX + separatorWidth;
        var draw = Gui.GetWindowDrawList();
        var color = Gui.GetColorU32(ImGuiCol.Text);
        Gui.Dummy(new Vector2(availableWidth, height));
        void Text(string text, float x, float clipLeft, float clipRight)
        {
            draw.PushClipRect(new Vector2(clipLeft, origin.Y), new Vector2(Math.Max(clipLeft, clipRight), origin.Y + height), true);
            try { draw.AddText(new Vector2(x, origin.Y + 8 * dpi), color, text); }
            finally { draw.PopClipRect(); }
        }
        Text(scale.ToString("P"), origin.X + 8 * dpi, origin.X, separatorX);
        draw.AddRectFilled(new Vector2(separatorX + 2 * dpi, origin.Y + 2 * dpi),
            new Vector2(separatorX + 3 * dpi, origin.Y + height - 2 * dpi), Gui.GetColorU32(ImGuiCol.Border));
        var widthText = string.Format(EditorLocalization.Text("W: {0}"), width);
        var otherText = string.Format(EditorLocalization.Text(heightmap ? "L: {0}" : "H: {0}"), otherDimension);
        var firstTextX = dimensionsX + 8 * dpi;
        Text(widthText, firstTextX, dimensionsX, origin.X + availableWidth);
        Text(otherText, firstTextX + Gui.CalcTextSize(widthText).X + 10 * dpi, dimensionsX, origin.X + availableWidth);
    }
}
