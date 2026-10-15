using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private unsafe Vector2 MeasureHierarchyTreeText(string text, uint weight)
    {
        if (CanDrawNativeWidgetText)
            return (nativeWidgetText ??= new()).Measure(text, Gui.GetFontSize(), Gui.GetStyle().FontScaleDpi, weight);
        var bold = weight == 700 && packagePropertiesBoldFont.Handle != null;
        // A zero size preserves the current logical font size. GetFontSize is
        // already DPI-scaled and would apply that scale a second time on push.
        if (bold) Gui.PushFont(packagePropertiesBoldFont, 0);
        try { return Gui.CalcTextSize(text); }
        finally { if (bold) Gui.PopFont(); }
    }
    private unsafe void DrawHierarchyTreeText(string text, Vector2 origin, Vector2 minimum, Vector2 maximum, uint? color, uint weight)
    {
        if (CanDrawNativeWidgetText)
        {
            var layout = (nativeWidgetText ??= new()).Get(text, Gui.GetFontSize(), Gui.GetStyle().FontScaleDpi,
                1_000_000, false, weight, color ?? Gui.GetColorU32(ImGuiCol.Text));
            PaintNativeWidget(layout, origin, minimum, maximum);
            LogNativeWidgetText(text, origin);
            return;
        }
        var bold = weight == 700 && packagePropertiesBoldFont.Handle != null;
        if (bold) Gui.PushFont(packagePropertiesBoldFont, 0);
        try { DrawNativeTreeText(text, origin, minimum, maximum, color); }
        finally { if (bold) Gui.PopFont(); }
    }
}
