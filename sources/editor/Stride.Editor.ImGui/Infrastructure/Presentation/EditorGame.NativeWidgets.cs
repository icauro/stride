using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private WindowsWidgetText? nativeWidgetText;
    private bool CanDrawNativeWidgetText => !IsEditorGpuRetirementPending && !IsEditorGpuRetirementFinalizing
        && !IsEditorGraphicsRecovering && scriptGlyphImages?.IsDraining != true;
    private unsafe uint NativeWidgetWeight => packagePropertiesBoldFont.Handle != null && Gui.GetFont().Handle == packagePropertiesBoldFont.Handle ? 700u : 400u;
    private WindowsFormattedText NativeWidgetLayout(string text, float width = 1_000_000, bool wrap = false, uint? color = null, bool underline = false)
        => (nativeWidgetText ??= new()).Get(text, Gui.GetFontSize(), Gui.GetStyle().FontScaleDpi, width, wrap,
            NativeWidgetWeight, color ?? Gui.GetColorU32(ImGuiCol.Text), underline);
    private Vector2 MeasureNativeLabel(string text) => CanDrawNativeWidgetText
        ? (nativeWidgetText ??= new()).Measure(text, Gui.GetFontSize(), Gui.GetStyle().FontScaleDpi, NativeWidgetWeight) : Gui.CalcTextSize(text);
    private unsafe void DrawNativeLabel(string text)
    {
        if (!CanDrawNativeWidgetText) { Gui.TextUnformatted(text); return; }
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems) return;
        var position = window.DC.CursorPos + new Vector2(0, window.DC.CurrLineTextBaseOffset);
        var wrap = window.DC.TextWrapPos >= 0;
        var width = wrap ? Math.Max(1, ImGuiP.CalcWrapWidthForPos(window.DC.CursorPos, window.DC.TextWrapPos)) : 1_000_000;
        var layout = NativeWidgetLayout(text, width, wrap);
        var bounds = new ImRect(position, position + layout.Size);
        ImGuiP.ItemSize(layout.Size, 0);
        if (ImGuiP.ItemAdd(bounds, 0))
        {
            PaintNativeWidget(layout, position, window.InnerClipRect.Min, window.InnerClipRect.Max);
            LogNativeWidgetText(text, position);
        }
    }
    private bool DrawNativeButton(string label, Vector2 size = default)
    {
        if (!CanDrawNativeWidgetText) return Gui.Button(label, size);
        return DrawNativeButtonEx(label, size, ImGuiButtonFlags.None);
    }
    private bool DrawNativeSmallButton(string label)
    {
        if (!CanDrawNativeWidgetText) return Gui.SmallButton(label);
        var padding = Gui.GetStyle().FramePadding;
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(padding.X, 0));
        try { return DrawNativeButtonEx(label, default, (ImGuiButtonFlags)ImGuiButtonFlagsPrivate.AlignTextBaseLine); }
        finally { Gui.PopStyleVar(); }
    }
    private unsafe bool DrawNativeButtonEx(string label, Vector2 size, ImGuiButtonFlags flags)
    {
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems) return false;
        var identity = Gui.GetID(label); var style = Gui.GetStyle();
        var layout = NativeWidgetLayout(NativeVisibleWidgetLabel(label));
        var actualSize = ImGuiP.CalcItemSize(size, layout.Size.X + style.FramePadding.X * 2, layout.Size.Y + style.FramePadding.Y * 2);
        var position = window.DC.CursorPos;
        if ((flags & (ImGuiButtonFlags)ImGuiButtonFlagsPrivate.AlignTextBaseLine) != 0 && style.FramePadding.Y < window.DC.CurrLineTextBaseOffset)
            position.Y += window.DC.CurrLineTextBaseOffset - style.FramePadding.Y;
        var bounds = new ImRect(position, position + actualSize);
        ImGuiP.ItemSize(actualSize, style.FramePadding.Y);
        if (!ImGuiP.ItemAdd(bounds, identity)) return false;
        var hovered = false; var held = false;
        var pressed = ImGuiP.ButtonBehavior(bounds, identity, ref hovered, ref held, flags);
        ImGuiP.RenderNavCursor(bounds, identity);
        ImGuiP.RenderFrame(bounds.Min, bounds.Max, Gui.GetColorU32(held && hovered ? ImGuiCol.ButtonActive : hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button), true, style.FrameRounding);
        var innerMin = bounds.Min + style.FramePadding; var innerMax = bounds.Max - style.FramePadding;
        var origin = innerMin + Vector2.Max(Vector2.Zero, (innerMax - innerMin - layout.Size) * style.ButtonTextAlign);
        PaintNativeWidget(layout, origin, innerMin, innerMax);
        if (Gui.GetCurrentContext().LogEnabled) ImGuiP.LogSetNextTextDecoration("[", "]");
        LogNativeWidgetText(NativeVisibleWidgetLabel(label), origin);
        return pressed;
    }
    private unsafe bool DrawNativeTextLink(string label)
    {
        if (!CanDrawNativeWidgetText) return Gui.TextLink(label);
        var window = ImGuiP.GetCurrentWindow();
        if (window.SkipItems) return false;
        var identity = Gui.GetID(label);
        var layout = NativeWidgetLayout(NativeVisibleWidgetLabel(label), color: Gui.GetColorU32(ImGuiCol.TextLink), underline: true);
        var origin = window.DC.CursorPos + new Vector2(0, window.DC.CurrLineTextBaseOffset);
        var bounds = new ImRect(origin, origin + layout.Size);
        ImGuiP.ItemSize(layout.Size, 0);
        if (!ImGuiP.ItemAdd(bounds, identity)) return false;
        var hovered = false; var held = false;
        var pressed = ImGuiP.ButtonBehavior(bounds, identity, ref hovered, ref held);
        ImGuiP.RenderNavCursor(bounds, identity);
        if (hovered) Gui.SetMouseCursor(ImGuiMouseCursor.Hand);
        PaintNativeWidget(layout, origin, bounds.Min, bounds.Max);
        LogNativeWidgetText(NativeVisibleWidgetLabel(label), origin);
        return pressed;
    }
    private void PaintNativeWidget(WindowsFormattedText layout, Vector2 origin, Vector2 minimum, Vector2 maximum)
    {
        var window = ImGuiP.GetCurrentWindow();
        // BeginMenuBar replaces the draw-list clip with the menu strip, which is
        // outside InnerClipRect. Respect that active clip just as native glyphs do.
        var clipMin = Vector2.Max(minimum, Gui.GetClipRectMin(window.DrawList));
        var clipMax = Vector2.Min(maximum, Gui.GetClipRectMax(window.DrawList));
        if (clipMax.X > clipMin.X && clipMax.Y > clipMin.Y)
        {
            window.DrawList.PushClipRect(clipMin, clipMax, true);
            try { layout.Draw(window.DrawList, GraphicsContext.CommandList, GetScriptGlyphImages(), origin, clipMin, clipMax); }
            finally { window.DrawList.PopClipRect(); }
        }
    }
    private static unsafe void LogNativeWidgetText(string text, Vector2 position)
    {
        if (!Gui.GetCurrentContext().LogEnabled || text.Length == 0) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        fixed (byte* first = bytes) ImGuiP.LogRenderedText(&position, first, first + bytes.Length);
    }
    private static string NativeVisibleWidgetLabel(string label)
    {
        var suffix = label.IndexOf("##", StringComparison.Ordinal);
        return suffix < 0 ? label : label[..suffix];
    }
    private void DisposeNativeWidgetText() { nativeWidgetText?.Dispose(); nativeWidgetText = null; }
}
