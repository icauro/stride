using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ImGuiStyle unscaledStyle;
    private readonly EditorViewportStylePolicy viewportStylePolicy = new();
    private float editorDpiScale;
    private EditorTheme appliedTheme;
    private int appliedThemeRevision;

    private static float UiScale(float value) => value * Gui.GetStyle().FontScaleDpi;
    private static System.Numerics.Vector2 UiScale(float width, float height) =>
        new(UiScale(width), UiScale(height));
    private static System.Numerics.Vector2 UiScale(System.Numerics.Vector2 value) =>
        value * Gui.GetStyle().FontScaleDpi;

    private unsafe void InitializeDpiScaling()
    {
        // Capture after all shell styling. Always scale this original copy rather
        // than repeatedly rounding the style left by the previous monitor.
        appliedTheme = preferences?.Current.Theme ?? EditorTheme.ExpressionDark;
        EditorThemes.Apply(appliedTheme);
        unscaledStyle = *Gui.GetStyle().Handle;
        viewportStylePolicy.SetBaseline(unscaledStyle);
        UpdateDpiScaling();
    }

    private unsafe void UpdateDpiScaling()
    {
        var theme = preferences?.Current.Theme ?? EditorTheme.ExpressionDark;
        if (theme != appliedTheme || appliedThemeRevision != EditorHotReload.Revision)
        {
            *Gui.GetStyle().Handle = unscaledStyle;
            EditorThemes.Apply(theme);
            unscaledStyle = *Gui.GetStyle().Handle;
            viewportStylePolicy.SetBaseline(unscaledStyle);
            appliedTheme = theme; editorDpiScale = 0;
            appliedThemeRevision = EditorHotReload.Revision;
        }
        var scale = WindowsDpi.GetScale(Window.NativeWindow.Handle);
        if (scale == editorDpiScale) return;
        editorDpiScale = scale;
        var style = Gui.GetStyle();
        *style.Handle = unscaledStyle;
        style.ScaleAllSizes(scale);
        // ImGui's dynamic font atlas requests glyphs at their rendered DPI size;
        // the renderer uploads those requests without rebuilding the font list.
        style.FontScaleDpi = scale;
        viewportStylePolicy.Apply(scale);
        Gui.GetMainViewport().DpiScale = scale;
        // IO and engine render targets continue to use real backbuffer pixels.
        // DisplayFramebufferScale stays one, matching normalized Stride input.
    }
    private void ApplyViewportStyle(float scale) => viewportStylePolicy.Apply(scale);
}
