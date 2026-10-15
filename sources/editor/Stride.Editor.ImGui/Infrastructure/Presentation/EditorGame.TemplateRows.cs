using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Core.IO;
using Stride.Core.Assets.Templates;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void PaintTemplateRow(string name, TemplateDescription? template, Vector2 minimum, Vector2 maximum, string? footer = null)
    {
        var dpi = Gui.GetStyle().FontScaleDpi;
        var origin = minimum + new Vector2(68, 2) * dpi;
        var width = Math.Max(1, maximum.X - origin.X - 2 * dpi);
        var titleSize = 16 * dpi;
        var descriptionSize = Gui.GetFontSize();
        var defaultIcon = Path.Combine(AppContext.BaseDirectory, "Icons", "template-default.png");
        var icon = template?.Icon;
        var path = defaultIcon;
        if (!string.IsNullOrEmpty(icon?.ToString()))
        {
            try { path = Path.GetFullPath(UPath.Combine(template!.FullPath.GetFullDirectory(), icon!).ToOSPath()); }
            catch (ArgumentException) { path = defaultIcon; }
            catch (NotSupportedException) { path = defaultIcon; }
        }
        PaintTemplateIcon(path, defaultIcon, minimum + new Vector2(2, 4) * dpi, new Vector2(48 * dpi));
        if (!CanDrawNativeWidgetText)
        {
            var draw = Gui.GetWindowDrawList();
            draw.PushClipRect(origin, maximum, true);
            try
            {
                draw.AddText(origin, Gui.GetColorU32(ImGuiCol.Text), name);
                if (!string.IsNullOrEmpty(footer))
                {
                    var size = Gui.CalcTextSize(footer);
                    draw.AddText(new Vector2(Math.Max(origin.X, maximum.X - 12 * dpi - size.X), maximum.Y - 2 * dpi - size.Y),
                        Gui.GetColorU32(ImGuiCol.Text), footer);
                }
            }
            finally { draw.PopClipRect(); }
            return;
        }
        var text = nativeWidgetText ??= new();
        var title = text.Get(name, titleSize, dpi, width, false, NativeWidgetWeight, Gui.GetColorU32(ImGuiCol.Text));
        PaintNativeWidget(title, origin, origin, maximum);
        LogNativeWidgetText(name, origin);
        var footerHeight = 0f;
        if (!string.IsNullOrEmpty(footer))
        {
            var count = text.Get(footer, descriptionSize, dpi, width, false, NativeWidgetWeight, Gui.GetColorU32(ImGuiCol.Text), italic: true);
            footerHeight = count.Height;
            var countOrigin = new Vector2(Math.Max(origin.X, maximum.X - 12 * dpi - count.Width), maximum.Y - 2 * dpi - count.Height);
            PaintNativeWidget(count, countOrigin, origin, maximum - new Vector2(2 * dpi));
            LogNativeWidgetText(footer, countOrigin);
        }
        var description = template?.Description;
        if (string.IsNullOrEmpty(description)) return;
        var descriptionOrigin = origin + new Vector2(0, title.Size.Y);
        var availableHeight = Math.Max(0, maximum.Y - descriptionOrigin.Y - 2 * dpi - footerHeight);
        if (availableHeight == 0) return;
        Vector2 Measure(string value) => text.Get(value, descriptionSize, dpi, width, true, NativeWidgetWeight, Gui.GetColorU32(ImGuiCol.Text)).Size;
        var fitted = FitTemplateDescription(description, availableHeight, Measure);
        var layout = text.Get(fitted, descriptionSize, dpi, width, true, NativeWidgetWeight, Gui.GetColorU32(ImGuiCol.Text));
        PaintNativeWidget(layout, descriptionOrigin, descriptionOrigin, maximum - new Vector2(2 * dpi, 2 * dpi + footerHeight));
        LogNativeWidgetText(description, descriptionOrigin);
    }
    private void PaintTemplateIcon(string path, string fallback, Vector2 origin, Vector2 bounds)
    {
        if (renderer == null || IsEditorGraphicsRecovering || IsEditorGpuRetirementPending || IsEditorGpuRetirementFinalizing) return;
        var cache = templateImages ??= new(GraphicsDevice, renderer, RetireEditorPreview);
        if (!cache.TryGet(path, frames, out var image, out var size) && !cache.TryGet(fallback, frames, out image, out size)) return;
        if (size.X <= 0 || size.Y <= 0) return;
        var factor = Math.Min(bounds.X / size.X, bounds.Y / size.Y);
        var actual = size * factor; var position = origin + (bounds - actual) * 0.5f;
        Gui.GetWindowDrawList().AddImage(image, position, position + actual);
    }
    private static string FitTemplateDescription(string value, float height, Func<string, Vector2> measure)
    {
        if (measure(value).Y <= height) return value;
        if (measure("…").Y > height) return "";
        var elements = StringInfo.ParseCombiningCharacters(value);
        var lower = 0; var upper = elements.Length;
        while (lower < upper)
        {
            var count = (lower + upper + 1) / 2;
            var end = count == elements.Length ? value.Length : elements[count];
            if (measure(value[..end].TrimEnd() + "…").Y <= height) lower = count;
            else upper = count - 1;
        }
        var limit = lower == elements.Length ? value.Length : elements[lower];
        var prefix = value[..limit].TrimEnd();
        // Native description TextBlock uses WordEllipsis, retaining complete words where they fit.
        var boundary = prefix.LastIndexOfAny([' ', '\t', '\r', '\n']);
        if (boundary > 0 && limit < value.Length && !char.IsWhiteSpace(value[limit])) prefix = prefix[..boundary].TrimEnd();
        return prefix + "…";
    }
}
