using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<(string Path, float FontSize, float Dpi, bool Native), string> recentPathTrimming = [];
    private bool DrawRecentProjectMenuRow(RecentProjectMetadata item)
    {
        var dpi = Gui.GetStyle().FontScaleDpi;
        var fontSize = Gui.GetFontSize(); var native = CanDrawNativeWidgetText;
        var key = (item.Path, fontSize, dpi, native);
        if (!recentPathTrimming.TryGetValue(key, out var path))
        {
            if (recentPathTrimming.Count >= 64) recentPathTrimming.Clear();
            float Measure(string value) => native ? (nativeWidgetText ??= new()).Measure(value, fontSize, dpi, 400).X : Gui.CalcTextSize(value).X;
            path = NativeMiddlePathTrimming.Fit(item.Path, 320 * dpi, Measure);
            recentPathTrimming.Add(key, path);
        }
        var height = Math.Max(Gui.GetTextLineHeight(), MeasureNativeLabel(path).Y);
        var clicked = Gui.Selectable("##RecentProjectMenuRow", false,
            (ImGuiSelectableFlags)(ImGuiSelectableFlagsPrivate.SelectOnRelease | ImGuiSelectableFlagsPrivate.NoSetKeyOwner | ImGuiSelectableFlagsPrivate.SetNavIdOnHover | ImGuiSelectableFlagsPrivate.SpanAvailWidth),
            new Vector2(350 * dpi, height));
        if (Gui.IsItemVisible())
        {
            var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
            var pathOrigin = minimum + new Vector2(30 * dpi, 0);
            var pathMaximum = Vector2.Min(maximum, pathOrigin + new Vector2(320 * dpi, height));
            if (native)
            {
                var text = nativeWidgetText ??= new(); var color = Gui.GetColorU32(ImGuiCol.Text);
                var version = text.Get(item.Version, fontSize, dpi, 30 * dpi, false, 700, color);
                PaintNativeWidget(version, minimum, minimum, pathOrigin + new Vector2(0, height));
                var layout = text.Get(path, fontSize, dpi, 320 * dpi, false, 400, color);
                PaintNativeWidget(layout, pathOrigin, pathOrigin, pathMaximum);
                LogNativeWidgetText(item.Version, minimum); LogNativeWidgetText(path, pathOrigin);
            }
            else
            {
                var draw = Gui.GetWindowDrawList();
                draw.PushClipRect(minimum, pathOrigin + new Vector2(0, height), true);
                var bold = PushPackagePropertiesBoldFont(true);
                try { draw.AddText(minimum, Gui.GetColorU32(ImGuiCol.Text), item.Version); }
                finally { if (bold) Gui.PopFont(); draw.PopClipRect(); }
                draw.PushClipRect(pathOrigin, pathMaximum, true);
                try { draw.AddText(pathOrigin, Gui.GetColorU32(ImGuiCol.Text), path); }
                finally { draw.PopClipRect(); }
            }
        }
        return clicked;
    }
}
