using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private WindowsImGuiTabTextHooks? nativeTabTextHooks;
    private void InitializeNativeTabText()
    {
        nativeTabTextHooks ??= WindowsImGuiTabTextHooks.TryCreate(MeasureNativeTab, PaintNativeTab);
    }
    private void DisposeNativeTabText() { nativeTabTextHooks?.Dispose(); nativeTabTextHooks = null; }
    private void PumpNativeTabTextFailure()
    {
        var failure = nativeTabTextHooks?.TakeFailure();
        if (failure == null) return;
        // Never throw across a native callback. Re-enter the existing device-failure path
        // only after native layout/render has returned to the owner-thread frame boundary.
        EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, failure);
        outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Native tab text", failure.ToString());
        DisposeNativeTabText();
    }
    private Vector2? MeasureNativeTab(string text, float fontSize)
    {
        if (!CanDrawNativeWidgetText) return null;
        return (nativeWidgetText ??= new()).Measure(text, fontSize, Gui.GetStyle().FontScaleDpi, NativeWidgetWeight);
    }
    private unsafe bool PaintNativeTab(string text, ImDrawListPtr draw, Vector2 minimum, Vector2 maximum, float ellipsisMax, uint color)
    {
        if (!CanDrawNativeWidgetText) return false;
        if (draw.Handle == null) return false;
        var fontSize = Gui.GetFontSize(); var dpi = Gui.GetStyle().FontScaleDpi;
        var width = Math.Max(0, maximum.X - minimum.X);
        var label = text;
        Vector2 Measure(string candidate) => (nativeWidgetText ??= new()).Measure(candidate, fontSize, dpi, NativeWidgetWeight);
        if (ellipsisMax >= minimum.X && Measure(text).X > width)
        {
            // Trim only presentation. The original visible UTF8 remains in native names/logging/IDs.
            const string ellipsis = "…";
            var available = Math.Max(0, Math.Min(maximum.X, ellipsisMax) - minimum.X);
            if (Measure(ellipsis).X > available) label = "";
            else
            {
                var boundaries = StringInfo.ParseCombiningCharacters(text);
                var count = boundaries.Length;
                label = ellipsis;
                // Measure whole shaped candidates: ligatures, script direction and fallback remain coupled.
                for (var kept = count - 1; kept >= 0; kept--)
                {
                    var candidate = text[..boundaries[kept]] + ellipsis;
                    if (Measure(candidate).X <= available) { label = candidate; break; }
                }
            }
        }
        var layout = (nativeWidgetText ??= new()).Get(label, fontSize, dpi, 1_000_000, false, NativeWidgetWeight, color);
        // Native title bars and docking may draw through a different display draw list
        // from the current content window. Its actual clip stack owns these pixels.
        var clippedMinimum = Vector2.Max(minimum, Gui.GetClipRectMin(draw));
        var clippedMaximum = Vector2.Min(maximum, Gui.GetClipRectMax(draw));
        if (clippedMaximum.X <= clippedMinimum.X || clippedMaximum.Y <= clippedMinimum.Y) return true;
        draw.PushClipRect(clippedMinimum, clippedMaximum, true);
        try { layout.Draw(draw, GraphicsContext.CommandList, GetScriptGlyphImages(), minimum, clippedMinimum, clippedMaximum); }
        finally { draw.PopClipRect(); }
        return true;
    }
}
